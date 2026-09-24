# Runs early in every repair and upgrade of the zaprett MSI (immediate custom action ZaprettCloseUi, before
# CostInitialize), as the user who started the setup (with the filtered token when the setup was started by a double
# click): InstallValidate finds the files held by the tray UI (zaprett-ui.exe) of that user and, since the UI has a
# window, would show the "files in use" dialog. The deferred ZaprettPrepare (LocalSystem) closes it too, but only
# later, after InstallValidate. So this script closes the user's own zaprett-ui.exe from the install folder first
# (a user may end their own processes without elevation) and records the sessions it closed it in, in the user's
# own registry: HKCU\Software\zaprett\UiRelaunchPending = "<unix time>;<session>,<session>". ZaprettPrepare reads
# that value from every loaded user hive, keeps only sessions of that user and adds them to run\ui-relaunch.json,
# so the service brings the tray back. The install folder comes from HKLM\SOFTWARE\zaprett\InstallDir.
# On a real removal it runs too (the maintenance dialog "Remove" would otherwise show "files in use"), from the copy
# ZAPRETT_CLOSE_UI_REMOVE that build.ps1 starts with "$NoRelaunch = $true": then nothing is recorded.
# The MSI embeds this file as -EncodedCommand (build.ps1): self-contained, ASCII. Always exit code 0.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# ANSI bytes into the MSI log (see prepare.ps1)
$script:LogOut = [Console]::OpenStandardOutput()
$script:Ansi = [Text.Encoding]::Default
function Write-Log([string]$Text) {
    $sb = New-Object Text.StringBuilder
    foreach ($ch in ("zaprett-close-ui: $Text").ToCharArray()) {
        if ([int]$ch -lt 128 -or $script:Ansi.GetString($script:Ansi.GetBytes([string]$ch)) -eq [string]$ch) { [void]$sb.Append($ch) }
        else { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) }
    }
    $bytes = $script:Ansi.GetBytes($sb.ToString() + "`r`n")
    $script:LogOut.Write($bytes, 0, $bytes.Length)
    $script:LogOut.Flush()
}

function Test-UnderDir([string]$Path, [string]$Dir) {
    if (-not $Path) { return $false }
    try { $p = [IO.Path]::GetFullPath($Path.Trim().Trim('"')) } catch { return $false }
    return $p.StartsWith($Dir + '\', [StringComparison]::OrdinalIgnoreCase)
}

try {
    $reg = Get-ItemProperty -Path 'HKLM:\SOFTWARE\zaprett' -Name InstallDir -ErrorAction SilentlyContinue
    if ($null -eq $reg -or -not $reg.InstallDir) { Write-Log 'no installed zaprett'; exit 0 }
    $dir = [IO.Path]::GetFullPath($reg.InstallDir).TrimEnd('\')
    $me = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sessions = New-Object Collections.Generic.List[int]
    foreach ($p in @(Get-Process -Name 'zaprett-ui' -ErrorAction SilentlyContinue)) {
        # the path of another user's process cannot be read without elevation: those are left to ZaprettPrepare
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if (-not (Test-UnderDir $path $dir)) { continue }
        try {
            Stop-Process -Id $p.Id -Force
            Wait-Process -Id $p.Id -Timeout 10 -ErrorAction SilentlyContinue
            Write-Log "closed zaprett-ui (pid $($p.Id), session $($p.SessionId)) of $($me.Name)"
            if (-not $sessions.Contains($p.SessionId)) { $sessions.Add($p.SessionId) }
        }
        catch { Write-Log "WARNING: could not close zaprett-ui (pid $($p.Id)): $($_.Exception.Message)" }
    }
    if ($sessions.Count -eq 0) { Write-Log "no zaprett-ui of $($me.Name) running"; exit 0 }
    if ($NoRelaunch) { Write-Log 'removal: no tray relaunch recorded'; exit 0 }
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $value = "$now;" + (($sessions | Sort-Object) -join ',')
    # not New-Item -Force: on an existing key it drops every value in it
    if (-not (Test-Path -LiteralPath 'HKCU:\Software\zaprett')) { [void](New-Item -Path 'HKCU:\Software\zaprett') }
    New-ItemProperty -Path 'HKCU:\Software\zaprett' -Name 'UiRelaunchPending' -PropertyType String -Value $value -Force | Out-Null
    Write-Log "tray relaunch requested for session(s) $(($sessions | Sort-Object) -join ',')"
}
catch { Write-Log "WARNING: $($_.Exception.Message)" }
exit 0
