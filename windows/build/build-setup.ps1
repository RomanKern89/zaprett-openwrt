<#
.SYNOPSIS
  Builds zaprett-<version>-x64-setup.exe around a finished MSI (windows/setup, docs/BUILD-WIN.md).

.DESCRIPTION
  The bootstrapper asks for administrator rights right after the double click and then runs the embedded MSI with its
  full UI (ZERR-058: the plain MSI asks only after "Install", and an unanswered prompt ends the setup as interrupted).
  build.ps1 calls this after the MSI is ready; a release pipeline that signs the MSI first calls it with the SIGNED
  MSI, so the exe carries exactly the published package.
  Checks after the build: FileVersion of the exe is <version>.0, and the embedded resource "zaprett.msi" is byte for
  byte the given MSI (SHA-256), read back from the built exe.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/build-setup.ps1 -Msi windows/artifacts/dist/zaprett-0.1.3-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [string]$OutDir = '',
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$win = [IO.Path]::GetFullPath((Join-Path $here '..'))
$Msi = [IO.Path]::GetFullPath($Msi)
if (-not (Test-Path -LiteralPath $Msi)) { throw "MSI not found: $Msi" }
if (-not $OutDir) { $OutDir = Split-Path -Parent $Msi }
$OutDir = [IO.Path]::GetFullPath($OutDir)

function Write-Step([string]$Text) { Write-Host "[setup] $Text" }

function Get-Dotnet {
    if ($env:DOTNET_EXE) { return $env:DOTNET_EXE }
    if (Test-Path -LiteralPath 'C:\dotnet10\dotnet.exe') { return 'C:\dotnet10\dotnet.exe' }
    return 'dotnet'
}

# one value of the Property table of the MSI; every COM object is released, so the MSI is not left open
function Get-MsiProperty([string]$Path, [string]$Name) {
    $wi = $null; $db = $null; $view = $null; $rec = $null
    try {
        $wi = New-Object -ComObject WindowsInstaller.Installer
        $db = $wi.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $wi, @([string]$Path, 0))
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property='$Name'"))
        $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { throw "no $Name in the Property table of $Path" }
        $value = $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, 1)
        $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        return $value
    }
    finally {
        foreach ($o in @($rec, $view, $db, $wi)) {
            if ($null -ne $o) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($o) }
        }
    }
}

# ProductVersion: the version of the exe and the folder of the package cache; ProductCode: the exe asks Windows
# Installer whether the product is installed before it removes the cache of a failed setup
$version = Get-MsiProperty $Msi 'ProductVersion'
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "unexpected ProductVersion '$version' in $Msi" }
$productCode = Get-MsiProperty $Msi 'ProductCode'
if ($productCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { throw "unexpected ProductCode '$productCode' in $Msi" }
$msiSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $Msi).Hash.ToLowerInvariant()
$msiName = [IO.Path]::GetFileName($Msi)
$exeName = [IO.Path]::GetFileNameWithoutExtension($Msi) + '-setup.exe'
Write-Step "$msiName ($version, sha256 $msiSha) -> $exeName"

$work = Join-Path ([IO.Path]::GetTempPath()) ('zaprett-setup-build-' + [Guid]::NewGuid().ToString('N'))
try {
    $dotnet = Get-Dotnet
    $csproj = Join-Path $win 'setup\Zaprett.Setup.csproj'
    $buildArgs = @('build', $csproj, '-c', $Configuration, '-nologo', "-p:Version=$version",
        "-p:ZaprettMsi=$Msi", "-p:ZaprettMsiSha256=$msiSha", "-p:ZaprettProductCode=$productCode",
        '-p:Deterministic=true', '-p:ContinuousIntegrationBuild=true',
        # a doubled trailing backslash: with a space in TEMP Windows PowerShell quotes the argument, and a single '\'
        # before the closing quote would escape it and glue the next argument on
        "-p:OutputPath=$work\bin\\", "-p:IntermediateOutputPath=$work\obj\\")
    Write-Step ("$dotnet " + ($buildArgs -join ' '))
    $ErrorActionPreference = 'Continue'
    # to the host, not to the output: the only output of this script is the path of the exe
    & $dotnet @buildArgs | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) { throw "dotnet build of the bootstrapper failed with exit code $code" }

    $built = Join-Path $work 'bin\zaprett-setup.exe'
    if (-not (Test-Path -LiteralPath $built)) { throw "the build did not produce $built" }

    # checks on the built file, not on the inputs
    $fileVersion = (Get-Item -LiteralPath $built).VersionInfo.FileVersion
    if ($fileVersion -ne "$version.0") { throw "zaprett-setup.exe FileVersion is '$fileVersion', expected '$version.0'" }
    $bytes = [IO.File]::ReadAllBytes($built)
    $asm = [Reflection.Assembly]::Load($bytes)
    $res = $asm.GetManifestResourceStream('zaprett.msi')
    if ($null -eq $res) { throw 'zaprett-setup.exe carries no resource zaprett.msi' }
    $sha = [Security.Cryptography.SHA256]::Create()
    $embeddedSha = (($sha.ComputeHash($res) | ForEach-Object { $_.ToString('x2') }) -join '')
    $res.Dispose()
    if ($embeddedSha -ne $msiSha) { throw "embedded MSI sha256 $embeddedSha differs from $msiSha" }
    $meta = @{}
    foreach ($a in $asm.GetCustomAttributes([Reflection.AssemblyMetadataAttribute], $false)) { $meta[$a.Key] = $a.Value }
    if ($meta['ZaprettMsiSha256'] -ne $msiSha) { throw "ZaprettMsiSha256 metadata '$($meta['ZaprettMsiSha256'])' differs from $msiSha" }
    if ($meta['ZaprettMsiName'] -ne $msiName) { throw "ZaprettMsiName metadata '$($meta['ZaprettMsiName'])' differs from $msiName" }
    if ($meta['ZaprettMsiVersion'] -ne $version) { throw "ZaprettMsiVersion metadata '$($meta['ZaprettMsiVersion'])' differs from $version" }
    if ($meta['ZaprettProductCode'] -ne $productCode) { throw "ZaprettProductCode metadata '$($meta['ZaprettProductCode'])' differs from $productCode" }

    [void](New-Item -ItemType Directory -Force -Path $OutDir)
    $out = Join-Path $OutDir $exeName
    Copy-Item -LiteralPath $built -Destination $out -Force
    Write-Step ("{0}  {1:N1} MB, FileVersion {2}, embedded MSI sha256 OK" -f $exeName, ((Get-Item -LiteralPath $out).Length / 1MB), $fileVersion)
    Write-Output $out
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
