<#
.SYNOPSIS
  Downloads the pinned engine archives and extracts the Windows engine files (ARCHITECTURE-WIN W-6).

.DESCRIPTION
  For every engine in windows/engine/engine.lock.json:
    1. the release archive is taken from the cache (windows/artifacts/cache) or downloaded, and its size
       and sha256 must equal the lock; a cached archive that does not match is deleted and downloaded again;
    2. the release sha256sum.txt is downloaded (or cached), its sha256 must equal the lock, and for every
       file marked in_checksums its line must carry the same sha256 as the lock;
    3. only the listed files are extracted (killall.exe, mdig.exe, ip2net.exe are never taken),
       size and sha256 of each must equal the lock.
  Any mismatch stops the script with exit code 1 and nothing is left in the output directory.

  -SelfTest runs negative controls on copies of the cached archives: one flipped byte in the archive and
  one wrong sha256 in a copy of the lock must both fail.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/fetch-engine.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/fetch-engine.ps1 -Offline
  powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/fetch-engine.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string]$Lock = '',
    [string]$Cache = '',
    [string]$OutDir = '',
    [switch]$Offline,
    [switch]$SelfTest
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# $PSScriptRoot is not usable in param() defaults under Windows PowerShell 5.1 -File.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Lock) { $Lock = Join-Path $here '..\engine\engine.lock.json' }
if (-not $Cache) { $Cache = Join-Path $here '..\artifacts\cache' }
if (-not $OutDir) { $OutDir = Join-Path $here '..\artifacts\engine' }

function Write-Step([string]$Text) { Write-Host "[fetch-engine] $Text" }

function Get-BytesSha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Save-Url([string]$Url, [string]$Path) {
    if ($Offline) { throw "offline mode: $Path is not in the cache" }
    Write-Step "download $Url"
    $tmp = "$Path.part"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

# Returns the path of a cached file whose sha256 equals $Sha256; downloads it (once) when missing or wrong.
function Get-Pinned([string]$Url, [string]$Name, [string]$Sha256, [string]$CacheDir) {
    $path = Join-Path $CacheDir $Name
    if (Test-Path -LiteralPath $path) {
        $have = Get-FileSha256 $path
        if ($have -eq $Sha256) { Write-Step "cache ok  $Name"; return $path }
        Write-Step "cache MISMATCH $Name (sha256 $have), downloading again"
        Remove-Item -LiteralPath $path -Force
    }
    Save-Url $Url $path
    $have = Get-FileSha256 $path
    if ($have -ne $Sha256) {
        Remove-Item -LiteralPath $path -Force
        throw "sha256 mismatch for $Name`: expected $Sha256, got $have"
    }
    return $path
}

function Read-Checksums([string]$Path) {
    $map = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $map[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant() }
    }
    return $map
}

function Expand-Engine($Engine, [string]$CacheDir, [string]$Dest) {
    $archive = Get-Pinned $Engine.archive.url $Engine.archive.name $Engine.archive.sha256 $CacheDir
    $size = (Get-Item -LiteralPath $archive).Length
    if ($size -ne [int64]$Engine.archive.size) { throw "size mismatch for $($Engine.archive.name): expected $($Engine.archive.size), got $size" }

    $sumsName = "$($Engine.id)-$($Engine.version)-sha256sum.txt"
    $sums = Read-Checksums (Get-Pinned $Engine.checksums.url $sumsName $Engine.checksums.sha256 $CacheDir)

    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($f in $Engine.files) {
            if ($f.dest -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($f.dest)) { throw "unsafe dest in lock: $($f.dest)" }
            $entry = $zip.GetEntry($f.path)
            if ($null -eq $entry) { throw "$($f.path) is not in $($Engine.archive.name)" }
            $ms = New-Object IO.MemoryStream
            $s = $entry.Open()
            try { $s.CopyTo($ms) } finally { $s.Dispose() }
            $bytes = $ms.ToArray()
            $sha = Get-BytesSha256 $bytes
            if ($bytes.Length -ne [int64]$f.size) { throw "size mismatch for $($f.path): expected $($f.size), got $($bytes.Length)" }
            if ($sha -ne $f.sha256) { throw "sha256 mismatch for $($f.path): expected $($f.sha256), got $sha" }
            if ($f.in_checksums) {
                if (-not $sums.ContainsKey($f.path)) { throw "$($f.path) is missing in the release sha256sum.txt" }
                if ($sums[$f.path] -ne $sha) { throw "release sha256sum.txt disagrees for $($f.path): $($sums[$f.path])" }
            }
            $target = Join-Path $Dest (Join-Path $Engine.dest $f.dest)
            [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target))
            [IO.File]::WriteAllBytes($target, $bytes)
            Write-Step ("ok  {0,-28} {1}" -f (Join-Path $Engine.dest $f.dest), $sha)
        }
    }
    finally { $zip.Dispose() }
}

function Invoke-Fetch([string]$LockPath, [string]$CacheDir, [string]$Dest) {
    $lockData = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($lockData.schema -ne 1) { throw "unsupported engine.lock.json schema $($lockData.schema)" }
    [void](New-Item -ItemType Directory -Force -Path $CacheDir)
    $staging = "$Dest.staging"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    [void](New-Item -ItemType Directory -Force -Path $staging)
    try {
        foreach ($engine in $lockData.engines) {
            Write-Step "$($engine.id) $($engine.version) ($($engine.project))"
            Expand-Engine $engine $CacheDir $staging
        }
    }
    catch {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
    if (Test-Path -LiteralPath $Dest) { Remove-Item -LiteralPath $Dest -Recurse -Force }
    Move-Item -LiteralPath $staging -Destination $Dest
}

function Invoke-SelfTest {
    Invoke-Fetch $Lock $Cache $OutDir   # makes sure the cache is complete and valid
    $work = Join-Path ([IO.Path]::GetTempPath()) ("zaprett-fetch-selftest-" + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $work)
    $script:Offline = $true
    $failures = 0
    try {
        # 1. one flipped byte in a cached archive
        $cache1 = Join-Path $work 'cache-flip'
        Copy-Item -LiteralPath $Cache -Destination $cache1 -Recurse
        $lockData = Get-Content -LiteralPath $Lock -Raw -Encoding UTF8 | ConvertFrom-Json
        $zipPath = Join-Path $cache1 $lockData.engines[0].archive.name
        $bytes = [IO.File]::ReadAllBytes($zipPath)
        $bytes[[int]($bytes.Length / 2)] = $bytes[[int]($bytes.Length / 2)] -bxor 0x01
        [IO.File]::WriteAllBytes($zipPath, $bytes)
        try { Invoke-Fetch $Lock $cache1 (Join-Path $work 'out1'); Write-Step 'SELFTEST FAIL: flipped archive byte was accepted'; $failures++ }
        catch { Write-Step "SELFTEST ok: flipped archive byte rejected ($($_.Exception.Message))" }

        # 2. wrong sha256 of one extracted file in a copy of the lock
        $lock2 = Join-Path $work 'engine.lock.json'
        $text = [IO.File]::ReadAllText($Lock)
        $good = $lockData.engines[0].files[0].sha256
        $bad = $good.Substring(0, 63) + $(if ($good[63] -eq '0') { '1' } else { '0' })
        [IO.File]::WriteAllText($lock2, $text.Replace($good, $bad))
        try { Invoke-Fetch $lock2 $Cache (Join-Path $work 'out2'); Write-Step 'SELFTEST FAIL: wrong file sha256 was accepted'; $failures++ }
        catch { Write-Step "SELFTEST ok: wrong file sha256 rejected ($($_.Exception.Message))" }
        if (Test-Path -LiteralPath (Join-Path $work 'out2')) { Write-Step 'SELFTEST FAIL: output left after failure'; $failures++ }

        # 3. archive re-packed with one byte changed in winws.exe, archive sha256/size in the lock updated to match:
        #    the per-file sha256 (and the release sha256sum.txt) must still catch it
        $cache3 = Join-Path $work 'cache-repack'
        Copy-Item -LiteralPath $Cache -Destination $cache3 -Recurse
        $engine0 = $lockData.engines[0]
        $zip3 = Join-Path $cache3 $engine0.archive.name
        $z = [IO.Compression.ZipFile]::Open($zip3, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry = $z.GetEntry($engine0.files[0].path)
            $ms = New-Object IO.MemoryStream
            $s = $entry.Open(); try { $s.CopyTo($ms) } finally { $s.Dispose() }
            $data = $ms.ToArray()
            $data[100] = $data[100] -bxor 0x01
            $entry.Delete()
            $s = $z.CreateEntry($engine0.files[0].path).Open(); try { $s.Write($data, 0, $data.Length) } finally { $s.Dispose() }
        }
        finally { $z.Dispose() }
        $lock3 = Join-Path $work 'engine3.lock.json'
        $newSha = Get-FileSha256 $zip3
        $newSize = (Get-Item -LiteralPath $zip3).Length
        $text3 = $text.Replace($engine0.archive.sha256, $newSha).Replace('"size": ' + $engine0.archive.size + ',', '"size": ' + $newSize + ',')
        [IO.File]::WriteAllText($lock3, $text3)
        try { Invoke-Fetch $lock3 $cache3 (Join-Path $work 'out3'); Write-Step 'SELFTEST FAIL: re-packed archive with a changed winws.exe was accepted'; $failures++ }
        catch { Write-Step "SELFTEST ok: changed winws.exe inside a re-packed archive rejected ($($_.Exception.Message))" }
    }
    finally {
        $script:Offline = $false
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($failures -gt 0) { throw "self-test: $failures negative control(s) passed that must fail" }
    Write-Step 'SELFTEST PASSED'
}

try {
    if ($SelfTest) { Invoke-SelfTest } else { Invoke-Fetch $Lock $Cache $OutDir }
    Write-Step "done: $((Resolve-Path -LiteralPath $OutDir).Path)"
    exit 0
}
catch {
    Write-Host "[fetch-engine] ERROR: $($_.Exception.Message)"
    exit 1
}
