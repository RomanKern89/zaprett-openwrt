<#
.SYNOPSIS
  Checks that ARPSIZE of a built MSI is the installed size of its files (build.ps1 runs it on every build).

.DESCRIPTION
  build.ps1 sets ARPSIZE (KB) from the staged files. This check counts again, independently, from the File table of
  the MSI itself: ARPSIZE must be the sum of File.FileSize in KB, rounded up. It fails when ARPSIZE is missing, is
  not a number, or differs (a staged file left out of the package, or one packaged but not counted).
  Exit code 0 when they match, 1 otherwise.

.EXAMPLE
  check-arpsize.ps1 -Msi windows\artifacts\dist\zaprett-0.1.2-x64.msi
#>
param(
    [Parameter(Mandatory = $true)][string]$Msi
)

$ErrorActionPreference = 'Stop'
$M = [Reflection.BindingFlags]::InvokeMethod
$G = [Reflection.BindingFlags]::GetProperty
function Invoke-Com($o, [string]$n, $k, [object[]]$a) { $o.GetType().InvokeMember($n, $k, $null, $o, $a) }
$installer = New-Object -ComObject WindowsInstaller.Installer

function Get-Column($Db, [string]$Sql) {
    $v = Invoke-Com $Db 'OpenView' $M @($Sql)
    [void](Invoke-Com $v 'Execute' $M @())
    $values = New-Object Collections.Generic.List[string]
    while ($true) {
        $r = Invoke-Com $v 'Fetch' $M @()
        if ($null -eq $r) { break }
        $values.Add((Invoke-Com $r 'StringData' $G @(1)))
    }
    [void](Invoke-Com $v 'Close' $M @())
    return , $values
}

$db = Invoke-Com $installer 'OpenDatabase' $M @([IO.Path]::GetFullPath($Msi), 0)
$arp = Get-Column $db 'SELECT `Value` FROM `Property` WHERE `Property` = ''ARPSIZE'''
$sizes = Get-Column $db 'SELECT `FileSize` FROM `File`'
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)

$bytes = [long]0
foreach ($s in $sizes) { $bytes += [long]$s }
$expectedKb = [long][Math]::Ceiling($bytes / 1024)

if ($arp.Count -ne 1 -or $arp[0] -notmatch '^[0-9]+$') {
    Write-Host "check-arpsize: FAIL: ARPSIZE is missing or not a number ('$($arp -join ',')'); files: $($sizes.Count), $bytes bytes = $expectedKb KB"
    exit 1
}
if ([long]$arp[0] -ne $expectedKb) {
    Write-Host "check-arpsize: FAIL: ARPSIZE $($arp[0]) KB, but the $($sizes.Count) files of the package are $bytes bytes = $expectedKb KB"
    exit 1
}
Write-Host "check-arpsize: ARPSIZE $($arp[0]) KB = $($sizes.Count) files, $bytes bytes"
exit 0
