<#
.SYNOPSIS
  Builds the zaprett MSI: engine, publish of service / CLI / UI, data, WiX, SHA256SUMS (docs/BUILD-WIN.md).

.DESCRIPTION
  1. fetch-engine.ps1: pinned winws / winws2 files (engine.lock.json);
  2. dotnet test of windows/Zaprett.slnx (skip with -SkipTests);
  3. self-contained win-x64 publish of service, CLI and UI into ONE folder stage\app with one shared .NET
     runtime (ARCHITECTURE-WIN §12.1); an overlapping file that differs between the publishes stops the build;
  4. data from the router package (packages/zaprett/files/usr/share/zaprett: bundle\, guard\, presets.json);
  5. WiX v7 (installer/Zaprett.Installer.wixproj), one MSI per culture (ru-RU, en-US, zh-CN), same ProductCode;
  6. one multi-language MSI (ru-RU base + embedded en-US / zh-CN transforms, msi-languages.ps1):
     artifacts\dist\zaprett-<version>-x64.msi + SHA256SUMS.
  Every run publishes afresh from THIS working tree with -p:Version=<version>, and then checks that every zaprett
  exe/dll carries that version (FileVersion X.Y.Z.0, ProductVersion X.Y.Z[+commit]); a mismatch stops the build
  (check-versions.ps1). Each run works in its own artifacts\run-<time>-<pid> (removed on success), and only one
  build runs at a time (artifacts\build.lock).
  -PreviousMsi <MSI of the last release>: every component with the same key path must keep its GUID
  (check-components.ps1), because upgrades remove the old version after installing the new one
  (afterInstallExecute). Required for a release; without it the check is skipped with a warning.

  -StubUi: a placeholder zaprett-ui.exe is built from build\stub-ui instead of the real UI, so a test MSI keeps
  its final layout while the UI is not ready. Such an MSI is named ...-STUB-UI.msi and must never be released.

  The dotnet from $env:DOTNET_EXE is used, else C:\dotnet10\dotnet.exe if present, else dotnet on PATH.
  WiX v7 requires accepting the OSMF EULA: this script passes -p:AcceptEula=wix7 (see BUILD-WIN.md).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/build.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/build.ps1 -Version 1.0.1 -SkipTests -StubUi
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$Offline,
    [switch]$StubUi,
    # the MSI of the previous release: its component GUIDs must stay the same (check-components.ps1)
    [string]$PreviousMsi = '',
    [string]$OutDir = ''
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$win = [IO.Path]::GetFullPath((Join-Path $here '..'))
$repo = [IO.Path]::GetFullPath((Join-Path $win '..'))
$artifacts = Join-Path $win 'artifacts'
# every run works in its own folder: other builds (or a UI started from an old publish) cannot mix into it
$runDir = Join-Path $artifacts ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
$stage = Join-Path $runDir 'stage'
if (-not $OutDir) { $OutDir = Join-Path $artifacts 'dist' }
$Rid = 'win-x64'
$src = $win
$Cultures = @('ru-RU', 'en-US', 'zh-CN')

function Write-Step([string]$Text) { Write-Host "[build] $Text" }

function Get-Dotnet {
    if ($env:DOTNET_EXE) { return $env:DOTNET_EXE }
    if (Test-Path -LiteralPath 'C:\dotnet10\dotnet.exe') { return 'C:\dotnet10\dotnet.exe' }
    return 'dotnet'
}

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    Write-Step ("$Exe " + ($Arguments -join ' '))
    # Windows PowerShell 5.1 turns native stderr lines into errors; with 'Stop' a warning would abort the build.
    $ErrorActionPreference = 'Continue'
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

function Get-ProjectVersion {
    [xml]$props = Get-Content -LiteralPath (Join-Path $win 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode('//Version')
    if ($null -eq $node) { throw 'Directory.Build.props has no <Version>' }
    return $node.InnerText.Trim()
}

function Get-AssemblyNameOf([string]$Csproj) {
    [xml]$x = Get-Content -LiteralPath $Csproj -Raw
    $node = $x.SelectSingleNode('//AssemblyName')
    if ($null -eq $node) { return [IO.Path]::GetFileNameWithoutExtension($Csproj) }
    return $node.InnerText.Trim()
}

function New-CleanDir([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    [void](New-Item -ItemType Directory -Force -Path $Path)
}

function Get-Sha([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

# Publishes one project self-contained into $Dest; $AssemblyName forces the exe name when the project lacks it.
function Publish-Project([string]$Csproj, [string]$Dest, [string]$AssemblyName) {
    $tmp = Join-Path $runDir ('publish-' + [IO.Path]::GetFileNameWithoutExtension($Csproj))
    New-CleanDir $tmp
    $publishArgs = @('publish', $Csproj, '-c', $Configuration, '-r', $Rid, '--self-contained', 'true', '-o', $tmp, '-nologo',
        "-p:Version=$Version", '-p:DebugType=none', '-p:DebugSymbols=false', '-p:Deterministic=true',
        '-p:ContinuousIntegrationBuild=true', '-p:GenerateDocumentationFile=false',
        # ReadyToRun for every project: a faster service start after boot; all three publishes must use it, or a
        # later publish of a shared assembly (Zaprett.Core.dll) differs from the R2R one and stops the build
        '-p:PublishReadyToRun=true')
    Invoke-Checked $script:Dotnet $publishArgs
    $actual = Get-AssemblyNameOf $Csproj
    if ($AssemblyName -and $actual -ne $AssemblyName) {
        # A global -p:AssemblyName would leak into referenced projects; the apphost finds its dll by the
        # name embedded in it, so renaming only the exe is safe.
        Write-Step "WARNING: $Csproj has AssemblyName $actual, renaming $actual.exe to $AssemblyName.exe"
        Move-Item -LiteralPath (Join-Path $tmp "$actual.exe") -Destination (Join-Path $tmp "$AssemblyName.exe")
    }
    [void](New-Item -ItemType Directory -Force -Path $Dest)
    foreach ($f in Get-ChildItem -LiteralPath $tmp -Recurse -File) {
        $rel = $f.FullName.Substring($tmp.Length).TrimStart('\')
        $target = Join-Path $Dest $rel
        if (Test-Path -LiteralPath $target) {
            if ((Get-Sha $target) -ne (Get-Sha $f.FullName)) { throw "publish conflict: $rel differs between projects in $Dest" }
            continue
        }
        [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target))
        Copy-Item -LiteralPath $f.FullName -Destination $target
    }
    Remove-Item -LiteralPath $tmp -Recurse -Force
}

function Copy-Tree([string]$From, [string]$To) {
    if (-not (Test-Path -LiteralPath $From)) { throw "missing: $From" }
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $To))
    Copy-Item -LiteralPath $From -Destination $To -Recurse -Force
}

$script:Dotnet = Get-Dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (-not $Version) { $Version = Get-ProjectVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version '$Version' must be MAJOR.MINOR.PATCH (MSI ProductVersion)" }
Write-Step "zaprett $Version, dotnet $script:Dotnet"
# one build at a time: two builds share artifacts\ (published, stage, wix) and would mix their files
[void](New-Item -ItemType Directory -Force -Path $artifacts)
try {
    $script:BuildLock = [IO.File]::Open((Join-Path $artifacts 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
}
catch {
    throw "another build.ps1 is using $artifacts (artifacts\build.lock is held); wait for it to finish"
}
$head = (& git -C $win rev-parse --short HEAD 2>$null)
$dirty = @(& git -C $win status --porcelain -- . 2>$null).Count
Write-Step "sources: working tree $win (HEAD $head, $dirty changed/untracked paths under windows/)"

# 1. engine
$fetchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'fetch-engine.ps1'))
if ($Offline) { $fetchArgs += '-Offline' }
Invoke-Checked 'powershell.exe' $fetchArgs

# 2. tests
if (-not $SkipTests) {
    Invoke-Checked $script:Dotnet @('test', (Join-Path $win 'Zaprett.slnx'), '-c', $Configuration, '-nologo')
}

# 3. publish afresh from this working tree (never reused: a previous publish may come from other sources or another
#    version), then check that every zaprett binary carries $Version
$published = Join-Path $runDir 'published'
$publishing = "$published.new"
New-CleanDir $publishing
Publish-Project (Join-Path $src 'src\Zaprett.Service\Zaprett.Service.csproj') (Join-Path $publishing 'app') 'zaprett-svc'
Publish-Project (Join-Path $src 'src\Zaprett.Cli\Zaprett.Cli.csproj') (Join-Path $publishing 'app') 'zaprett'
$uiProject = Join-Path $src 'src\Zaprett.Ui\Zaprett.Ui.csproj'
$isStub = [bool]$StubUi
if ($StubUi) {
    Write-Step 'WARNING: -StubUi: building the placeholder UI instead of src/Zaprett.Ui'
    Publish-Project (Join-Path $here 'stub-ui/Zaprett.StubUi.csproj') (Join-Path $publishing 'app') 'zaprett-ui'
}
elseif (Test-Path -LiteralPath $uiProject) {
    Publish-Project $uiProject (Join-Path $publishing 'app') 'zaprett-ui'
}
else {
    throw 'src\Zaprett.Ui\Zaprett.Ui.csproj does not exist (use -StubUi for a test MSI with a placeholder UI)'
}
if (Test-Path -LiteralPath $published) { Remove-Item -LiteralPath $published -Recurse -Force }
Move-Item -LiteralPath $publishing -Destination $published
foreach ($exe in @('app\zaprett-svc.exe', 'app\zaprett.exe', 'app\zaprett-ui.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $exe))) { throw "publish did not produce $exe" }
}
& (Join-Path $here 'check-versions.ps1') -Dir (Join-Path $published 'app') -Version $Version -RequireReadyToRun
if ($LASTEXITCODE -ne 0) { throw "published binaries do not carry version $Version (check-versions.ps1)" }

# 4. stage: published binaries, engine, data, installer script
New-CleanDir $stage
Copy-Tree (Join-Path $published 'app') (Join-Path $stage 'app')
Copy-Tree (Join-Path $artifacts 'engine\engine') (Join-Path $stage 'engine')
Copy-Tree (Join-Path $artifacts 'engine\engine2') (Join-Path $stage 'engine2')
$share = Join-Path $repo 'packages\zaprett\files\usr\share\zaprett'
Copy-Tree (Join-Path $share 'bundle') (Join-Path $stage 'data\bundle')
Copy-Tree (Join-Path $share 'guard') (Join-Path $stage 'data\guard')
Copy-Item -LiteralPath (Join-Path $share 'presets.json') -Destination (Join-Path $stage 'data\presets.json')
[void](New-Item -ItemType Directory -Force -Path (Join-Path $stage 'tools'))
foreach ($script in @('installer-actions.ps1', 'launch-ui.ps1')) {
    Copy-Item -LiteralPath (Join-Path $win "installer\scripts\$script") -Destination (Join-Path $stage "tools\$script")
}
# every staged file goes into the MSI, so their sum is the installed size (ARPSIZE, KB, rounded up)
$stageBytes = (Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum
$arpSizeKb = [long][Math]::Ceiling($stageBytes / 1024)
Write-Step "installed size: $stageBytes bytes, ARPSIZE $arpSizeKb KB"


# 5. MSI: one per culture, sharing a ProductCode derived from the version (name-based, SHA-1)
$wixproj = Join-Path $win 'installer\Zaprett.Installer.wixproj'
$wixOut = Join-Path $runDir 'wix'
New-CleanDir $wixOut
$pcHash = [Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("zaprett-windows-msi-productcode/$Version"))
$pcBytes = New-Object byte[] 16
[Array]::Copy($pcHash, $pcBytes, 16)
$pcBytes[7] = ($pcBytes[7] -band 0x0F) -bor 0x50
$pcBytes[8] = ($pcBytes[8] -band 0x3F) -bor 0x80
$productCode = '{' + ([Guid]::new($pcBytes)).ToString().ToUpperInvariant() + '}'
Write-Step "ProductCode $productCode"
# installer/scripts/prepare.ps1, close-ui.ps1 and restore-ui.ps1 run before the old version is removed (or after a
# failed setup), when no file of the new package is on disk, so the MSI carries them as PowerShell -EncodedCommand
# (comment lines dropped; must stay ASCII). They reach WiX in a generated source file (EmbeddedScripts.wxs,
# properties ZAPRETT_PREPARE, ZAPRETT_CLOSE_UI, ZAPRETT_CLOSE_UI_REMOVE, ZAPRETT_RESTORE_UI), not as -p/-d values:
# together they would pass the 32767-character limit of the dotnet and wix command lines.
# $Prefix: a first line put before the script (a variable the script reads, e.g. close-ui.ps1 on a removal)
function Get-EncodedScript([string]$Name, [string]$Prefix = '') {
    $lines = [IO.File]::ReadAllLines((Join-Path $win "installer\scripts\$Name")) |
        Where-Object { $_.Trim() -ne '' -and -not $_.TrimStart().StartsWith('#') }
    if ($Prefix) { $lines = @($Prefix) + @($lines) }
    $text = ($lines -join "`n") + "`n"
    if ($text -match '[^\x09\x0A\x20-\x7E]') { throw "installer\scripts\$Name must be ASCII" }
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($text))
    # powershell.exe -NoLogo ... -EncodedCommand <b64> must fit a 32767-character command line with room to spare
    if ($b64.Length -gt 24000) { throw "$Name is too long for a command line ($($b64.Length) base64 chars)" }
    Write-Step "$Name embedded: $($b64.Length) base64 chars"
    return $b64
}
$embeddedWxs = Join-Path $wixOut 'EmbeddedScripts.wxs'
$embeddedText = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n" +
    "<!-- generated by build.ps1 from installer/scripts/prepare.ps1, close-ui.ps1 and restore-ui.ps1 -->`n" +
    "<Wix xmlns=`"http://wixtoolset.org/schemas/v4/wxs`">`n  <Fragment>`n" +
    "    <Property Id=`"ZAPRETT_PREPARE`" Value=`"$(Get-EncodedScript 'prepare.ps1')`" />`n" +
    "    <Property Id=`"ZAPRETT_CLOSE_UI`" Value=`"$(Get-EncodedScript 'close-ui.ps1')`" />`n" +
    "    <Property Id=`"ZAPRETT_CLOSE_UI_REMOVE`" Value=`"$(Get-EncodedScript 'close-ui.ps1' '$NoRelaunch = $true')`" />`n" +
    "    <Property Id=`"ZAPRETT_RESTORE_UI`" Value=`"$(Get-EncodedScript 'restore-ui.ps1')`" />`n" +
    "  </Fragment>`n</Wix>`n"
[IO.File]::WriteAllText($embeddedWxs, $embeddedText, (New-Object Text.UTF8Encoding($false)))
Invoke-Checked $script:Dotnet @('build', $wixproj, '-c', $Configuration, '-nologo', "-p:ProductVersion=$Version",
    "-p:ProductCode=$productCode", "-p:ZaprettEmbeddedWxs=$embeddedWxs", "-p:ZaprettArpSizeKb=$arpSizeKb",
    "-p:StageDir=$stage\", '-p:AcceptEula=wix7', "-p:OutputPath=$wixOut\", "-p:IntermediateOutputPath=$wixOut\obj\")

# 6. one multi-language MSI (ru-RU base + embedded en-US and zh-CN transforms), SHA256SUMS
[xml]$wixprojXml = Get-Content -LiteralPath $wixproj -Raw
$wixVersion = ($wixprojXml.Project.Sdk -split '/')[1]
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$wixExe = Join-Path $nugetRoot "wixtoolset.sdk\$wixVersion\tools\net472\x64\wix.exe"
if (-not (Test-Path -LiteralPath $wixExe)) { throw "wix.exe not found: $wixExe" }
foreach ($culture in $Cultures) {
    if (-not (Test-Path -LiteralPath (Join-Path $wixOut "$culture\zaprett.msi"))) { throw "WiX did not produce $culture\zaprett.msi" }
}
New-CleanDir $OutDir
$suffix = if ($isStub) { '-STUB-UI' } else { '' }
$name = "zaprett-$Version-x64$suffix.msi"
# Russian is the base (ARCHITECTURE-WIN §12.2): any system language without its own transform gets Russian
& (Join-Path $here 'msi-languages.ps1') -Wix $wixExe -Base (Join-Path $wixOut 'ru-RU\zaprett.msi') `
    -Other @{ 1033 = (Join-Path $wixOut 'en-US\zaprett.msi'); 2052 = (Join-Path $wixOut 'zh-CN\zaprett.msi') } `
    -Out (Join-Path $OutDir $name) -WorkDir (Join-Path $wixOut 'lang')
# upgrades remove the old version after installing the new one (afterInstallExecute): a component whose GUID changed
# since the previous release would lose its files on upgrade
if ($PreviousMsi) {
    & (Join-Path $here 'check-components.ps1') -Old $PreviousMsi -New (Join-Path $OutDir $name)
    if ($LASTEXITCODE -ne 0) { throw "component GUIDs differ from the previous release $PreviousMsi (check-components.ps1)" }
}
else {
    Write-Step 'WARNING: no -PreviousMsi: the component GUIDs were NOT compared with the previous release (required for a release)'
}
& (Join-Path $here 'check-arpsize.ps1') -Msi (Join-Path $OutDir $name)
if ($LASTEXITCODE -ne 0) { throw 'ARPSIZE does not match the files of the package (check-arpsize.ps1)' }
$sums = "$(Get-Sha (Join-Path $OutDir $name))  $name`n"
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS'), $sums, (New-Object Text.UTF8Encoding($false)))
Write-Step ("{0}  {1:N1} MB" -f $name, ((Get-Item -LiteralPath (Join-Path $OutDir $name)).Length / 1MB))
Write-Step "done: $OutDir"
if ($isStub) { Write-Step 'WARNING: this MSI contains a placeholder UI and must not be released' }
# the work folder of this run is only kept when the build fails (for diagnosis)
Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction SilentlyContinue
