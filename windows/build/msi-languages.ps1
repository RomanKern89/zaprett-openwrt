<#
.SYNOPSIS
  Makes one multi-language MSI: the base culture MSI plus embedded language transforms of the other cultures.

.DESCRIPTION
  For every other culture: wix msi transform (base -> culture MSI) produces <LANGID>.mst, which is stored in the
  _Storages table of a copy of the base MSI under the name <LANGID>; the Template summary property becomes
  "x64;<base LANGID>,<other LANGIDs>". Windows Installer then applies the transform that matches the user's UI
  language; TRANSFORMS=:1049 forces one. All cultures must share ProductCode, UpgradeCode and version
  (checked here, the build fails otherwise).

.EXAMPLE
  msi-languages.ps1 -Wix <wix.exe> -Base en-US.msi -Other @{ 1049 = 'ru-RU.msi' } -Out zaprett.msi -WorkDir tmp
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Wix,
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][hashtable]$Other,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$WorkDir
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Invoke-Com($Object, [string]$Member, [Reflection.BindingFlags]$Kind, [object[]]$Arguments) {
    return $Object.GetType().InvokeMember($Member, $Kind, $null, $Object, $Arguments)
}
$Method = [Reflection.BindingFlags]::InvokeMethod
$Get = [Reflection.BindingFlags]::GetProperty
$Set = [Reflection.BindingFlags]::SetProperty

$installer = New-Object -ComObject WindowsInstaller.Installer

function Get-MsiProperty([string]$Msi, [string]$Name) {
    $db = Invoke-Com $installer 'OpenDatabase' $Method @($Msi, 0)
    try {
        $view = Invoke-Com $db 'OpenView' $Method @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'")
        [void](Invoke-Com $view 'Execute' $Method @())
        $rec = Invoke-Com $view 'Fetch' $Method @()
        $value = if ($null -eq $rec) { $null } else { Invoke-Com $rec 'StringData' $Get @(1) }
        [void](Invoke-Com $view 'Close' $Method @())
        return $value
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db) }
}

function Get-Template([string]$Msi) {
    $si = Invoke-Com $installer 'SummaryInformation' $Get @($Msi, 0)
    return Invoke-Com $si 'Property' $Get @(7)
}

$baseTemplate = Get-Template $Base
if ($baseTemplate -notmatch '^x64;(\d+)$') { throw "unexpected Template '$baseTemplate' in $Base" }
$baseLang = $Matches[1]
$identity = @('ProductCode', 'UpgradeCode', 'ProductVersion') | ForEach-Object { "$_=$(Get-MsiProperty $Base $_)" }
foreach ($lang in $Other.Keys) {
    $msi = $Other[$lang]
    $theirs = @('ProductCode', 'UpgradeCode', 'ProductVersion') | ForEach-Object { "$_=$(Get-MsiProperty $msi $_)" }
    if (($identity -join ';') -ne ($theirs -join ';')) { throw "$msi differs from $Base in identity: $($theirs -join ';') vs $($identity -join ';')" }
    if ((Get-Template $msi) -ne "x64;$lang") { throw "$msi is not language $lang (Template $(Get-Template $msi))" }
}
Write-Host "[msi-languages] base $baseLang, $($identity -join ', ')"

[void](New-Item -ItemType Directory -Force -Path $WorkDir)
Copy-Item -LiteralPath $Base -Destination $Out -Force
$langs = New-Object Collections.Generic.List[string]
$langs.Add($baseLang)
$si = $null; $rec = $null; $view = $null
$db = Invoke-Com $installer 'OpenDatabase' $Method @($Out, 1)   # msiOpenDatabaseModeTransact
try {
    foreach ($lang in ($Other.Keys | Sort-Object)) {
        $mst = Join-Path $WorkDir "$lang.mst"
        & $Wix msi transform -acceptEula wix7 -t language -serr f -intermediateFolder (Join-Path $WorkDir "tmp-$lang") $Base $Other[$lang] -out $mst
        if ($LASTEXITCODE -ne 0) { throw "wix msi transform failed for $lang" }
        $view = Invoke-Com $db 'OpenView' $Method @('SELECT `Name`, `Data` FROM `_Storages`')
        [void](Invoke-Com $view 'Execute' $Method @())
        $rec = Invoke-Com $installer 'CreateRecord' $Method @(2)
        Invoke-Com $rec 'StringData' $Set @(1, "$lang") | Out-Null
        # PowerShell wraps values in PSObject; COM IDispatch needs the plain string
        [void](Invoke-Com $rec 'SetStream' $Method @(2, [string]$mst))
        [void](Invoke-Com $view 'Modify' $Method @(1, $rec))        # msiViewModifyInsert
        [void](Invoke-Com $view 'Close' $Method @())
        $langs.Add("$lang")
        Write-Host "[msi-languages] embedded transform $lang ($((Get-Item -LiteralPath $mst).Length) bytes)"
    }
    # the summary stream of a database opened for writing is changed through the database itself
    $si = Invoke-Com $db 'SummaryInformation' $Get @(20)
    Invoke-Com $si 'Property' $Set @(7, "x64;$($langs -join ',')") | Out-Null
    [void](Invoke-Com $si 'Persist' $Method @())
    [void](Invoke-Com $db 'Commit' $Method @())
}
finally {
    # every COM reference into the database keeps the file open
    foreach ($o in @($si, $rec, $view, $db)) { if ($null -ne $o) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) } }
    $si = $null; $rec = $null; $view = $null; $db = $null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
$final = Get-Template $Out
if ($final -ne "x64;$($langs -join ',')") { throw "Template was not written: $final" }
Write-Host "[msi-languages] $Out Template=$final"
