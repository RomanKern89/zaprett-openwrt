<#
.SYNOPSIS
  Checks that every zaprett binary in a publish folder carries the MSI version (build.ps1, step 3).

.DESCRIPTION
  zaprett*.exe / zaprett*.dll / Zaprett.*.dll must have FileVersion X.Y.Z.0 and ProductVersion X.Y.Z or X.Y.Z+<commit>
  (SourceLink appends the commit). Exit code 1 with the list of offenders otherwise, 0 when all match.

.EXAMPLE
  check-versions.ps1 -Dir windows\artifacts\published\app -Version 0.1.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Dir,
    [Parameter(Mandatory = $true)][string]$Version,
    # also require every zaprett / Zaprett.* .dll to be ReadyToRun (CLR header with a ManagedNativeHeader)
    [switch]$RequireReadyToRun
)

# ReadyToRun images carry a ManagedNativeHeader directory in their CLR (COR20) header.
function Test-ReadyToRun([string]$Path) {
    $b = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $magic = [BitConverter]::ToUInt16($b, $pe + 24)
    $dirs = $pe + 24 + $(if ($magic -eq 0x20B) { 112 } else { 96 })
    $clrRva = [BitConverter]::ToUInt32($b, $dirs + 14 * 8)
    if ($clrRva -eq 0) { return $false }
    $sections = $pe + 24 + [BitConverter]::ToUInt16($b, $pe + 20)
    for ($i = 0; $i -lt [BitConverter]::ToUInt16($b, $pe + 6); $i++) {
        $s = $sections + $i * 40
        $va = [BitConverter]::ToUInt32($b, $s + 12); $size = [BitConverter]::ToUInt32($b, $s + 8); $raw = [BitConverter]::ToUInt32($b, $s + 20)
        if ($clrRva -ge $va -and $clrRva -lt $va + $size) {
            $cor = $raw + ($clrRva - $va)
            return [BitConverter]::ToUInt32($b, $cor + 64) -ne 0   # ManagedNativeHeader.VirtualAddress
        }
    }
    return $false
}

$files = @(Get-ChildItem -LiteralPath $Dir -File | Where-Object {
        $_.Name -like 'zaprett*.exe' -or $_.Name -like 'zaprett*.dll' -or $_.Name -like 'Zaprett.*.dll' })
if ($files.Count -eq 0) { Write-Host "[check-versions] ERROR: no zaprett binaries in $Dir"; exit 1 }
$bad = 0
$commits = New-Object Collections.Generic.HashSet[string]
foreach ($f in $files) {
    $vi = $f.VersionInfo
    $fileOk = $vi.FileVersion -eq "$Version.0"
    $productOk = $vi.ProductVersion -eq $Version -or $vi.ProductVersion -like "$Version+*"
    if ($vi.ProductVersion -match '\+(.+)$') { [void]$commits.Add($Matches[1]) }
    if (-not ($fileOk -and $productOk)) {
        Write-Host ("[check-versions] MISMATCH {0}: FileVersion {1}, ProductVersion {2} (expected {3})" -f $f.Name, $vi.FileVersion, $vi.ProductVersion, $Version)
        $bad++
    }
    if ($RequireReadyToRun -and $f.Extension -eq '.dll' -and -not (Test-ReadyToRun $f.FullName)) {
        Write-Host "[check-versions] NOT ReadyToRun: $($f.Name)"
        $bad++
    }
}
if ($commits.Count -gt 1) { Write-Host "[check-versions] MISMATCH: binaries come from different commits: $($commits -join ', ')"; $bad++ }
if ($bad -gt 0) { Write-Host "[check-versions] $bad problem(s) in $($files.Count) binaries"; exit 1 }
Write-Host "[check-versions] ok: $($files.Count) binaries are $Version$(if ($RequireReadyToRun) { ', dlls ReadyToRun' })$(if ($commits.Count) { ' (+' + ($commits -join '') + ')' })"
exit 0
