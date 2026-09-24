<#
.SYNOPSIS
  Checks that a new MSI keeps the component GUIDs of the previous release (build.ps1 -PreviousMsi).

.DESCRIPTION
  The MSI upgrades with MajorUpgrade Schedule="afterInstallExecute": the new version is installed first and the old
  one removed afterwards. That is only safe when every component with the same key path (directory chain + key file,
  or the component id for registry / folder key paths) has the SAME ComponentId in both packages - otherwise the
  removal of the old version deletes files or registry values the new version has just installed.
  Exit code 1 with the list of differing components, 0 when all components present in both match. Components only in
  the old or only in the new package are listed for information (they are added / removed normally).

.EXAMPLE
  check-components.ps1 -Old zaprett-0.1.0-x64.msi -New windows\artifacts\dist\zaprett-0.1.1-x64.msi
#>
param(
    [Parameter(Mandatory = $true)][string]$Old,
    [Parameter(Mandatory = $true)][string]$New
)

$ErrorActionPreference = 'Stop'
$M = [Reflection.BindingFlags]::InvokeMethod
$G = [Reflection.BindingFlags]::GetProperty
function Invoke-Com($o, [string]$n, $k, [object[]]$a) { $o.GetType().InvokeMember($n, $k, $null, $o, $a) }
$installer = New-Object -ComObject WindowsInstaller.Installer

function Get-Rows($Db, [string]$Sql, [int]$Cols) {
    $v = Invoke-Com $Db 'OpenView' $M @($Sql)
    [void](Invoke-Com $v 'Execute' $M @())
    $rows = New-Object Collections.Generic.List[object]
    while ($true) {
        $r = Invoke-Com $v 'Fetch' $M @()
        if ($null -eq $r) { break }
        $rows.Add(@(1..$Cols | ForEach-Object { Invoke-Com $r 'StringData' $G @([int]$_) }))
    }
    [void](Invoke-Com $v 'Close' $M @())
    return , $rows
}

# key path of every component -> ComponentId
function Get-ComponentMap([string]$Msi) {
    $db = Invoke-Com $installer 'OpenDatabase' $M @([IO.Path]::GetFullPath($Msi), 0)
    try {
        $dirs = @{}
        foreach ($d in (Get-Rows $db 'SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`' 3)) { $dirs[$d[0]] = $d }
        $files = @{}
        foreach ($f in (Get-Rows $db 'SELECT `File`,`FileName` FROM `File`' 2)) { $files[$f[0]] = ($f[1] -split '\|')[-1] }
        $map = @{}
        foreach ($c in (Get-Rows $db 'SELECT `Component`,`ComponentId`,`Directory_`,`KeyPath` FROM `Component`' 4)) {
            $parts = New-Object Collections.Generic.List[string]
            $id = $c[2]
            while ($id -and $dirs.ContainsKey($id)) {
                $name = (($dirs[$id][2] -split ':')[-1] -split '\|')[-1]
                $parts.Insert(0, $name)
                $id = $dirs[$id][1]
            }
            $leaf = if ($c[3] -and $files.ContainsKey($c[3])) { $files[$c[3]] } else { '<' + $c[0] + '>' }
            $map[(($parts -join '\') + '\' + $leaf).ToLowerInvariant()] = $c[1]
        }
        return $map
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db) }
}

$a = Get-ComponentMap $Old
$b = Get-ComponentMap $New
$same = 0
$diff = New-Object Collections.Generic.List[string]
$onlyOld = @($a.Keys | Where-Object { -not $b.ContainsKey($_) } | Sort-Object)
$onlyNew = @($b.Keys | Where-Object { -not $a.ContainsKey($_) } | Sort-Object)
foreach ($k in ($a.Keys | Sort-Object)) {
    if (-not $b.ContainsKey($k)) { continue }
    if ($a[$k] -eq $b[$k]) { $same++ } else { $diff.Add("$k  $($a[$k]) -> $($b[$k])") }
}
Write-Host "[check-components] old $($a.Count), new $($b.Count) components; same GUID: $same; different GUID: $($diff.Count); only in old: $($onlyOld.Count); only in new: $($onlyNew.Count)"
foreach ($k in ($onlyOld | Select-Object -First 20)) { Write-Host "[check-components]   only in old (removed with the old version): $k" }
foreach ($k in ($onlyNew | Select-Object -First 20)) { Write-Host "[check-components]   only in new: $k" }
if ($diff.Count -gt 0) {
    foreach ($d in $diff) { Write-Host "[check-components] DIFFERENT GUID: $d" }
    Write-Host '[check-components] FAIL: an upgrade (afterInstallExecute) would delete these files of the new version'
    exit 1
}
Write-Host '[check-components] ok'
exit 0
