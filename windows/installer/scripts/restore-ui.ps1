# Runs in the setup client (immediate custom action ZaprettRestoreUi, Finish button of the UserExit and FatalError
# pages), as the user who started the setup, when a repair or upgrade ends WITHOUT success: the UAC prompt was
# declined, the setup was cancelled, or it failed. close-ui.ps1 may already have closed this user's tray and left
# HKCU\Software\zaprett\UiRelaunchPending = "<unix time>;<session>,..." for ZaprettPrepare; when the value is still
# there, ZaprettPrepare never took it (it never ran, or a rollback put everything back), so nobody brings the tray
# back but this script: it deletes the value and starts "zaprett-ui.exe --tray" of the install folder
# (HKLM\SOFTWARE\zaprett\InstallDir) when this session is among the recorded ones - not elevated: directly from a
# non-elevated client, through a one-shot scheduled task (RunLevel Limited) from an elevated one.
# The MSI embeds this file as -EncodedCommand (build.ps1): self-contained, ASCII. Always exit code 0.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# ANSI bytes into the MSI log (see prepare.ps1)
$script:LogOut = [Console]::OpenStandardOutput()
$script:Ansi = [Text.Encoding]::Default
function Write-Log([string]$Text) {
    $sb = New-Object Text.StringBuilder
    foreach ($ch in ("zaprett-restore-ui: $Text").ToCharArray()) {
        if ([int]$ch -lt 128 -or $script:Ansi.GetString($script:Ansi.GetBytes([string]$ch)) -eq [string]$ch) { [void]$sb.Append($ch) }
        else { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) }
    }
    $bytes = $script:Ansi.GetBytes($sb.ToString() + "`r`n")
    $script:LogOut.Write($bytes, 0, $bytes.Length)
    $script:LogOut.Flush()
}

function Start-Unelevated([string]$Exe, [string]$Arguments) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $admin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) {
        Start-Process -FilePath $Exe -ArgumentList $Arguments -WorkingDirectory (Split-Path -Parent $Exe)
        return 'directly'
    }
    # an elevated client would start an elevated tray: a one-shot task with the filtered token instead
    $name = 'zaprett-restore-ui-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    try {
        $action = New-ScheduledTaskAction -Execute $Exe -Argument $Arguments -WorkingDirectory (Split-Path -Parent $Exe)
        $principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $name
        Start-Sleep -Seconds 2
    }
    finally { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue }
    return 'through a scheduled task (not elevated)'
}

try {
    $key = 'HKCU:\Software\zaprett'
    $value = (Get-ItemProperty -Path $key -Name 'UiRelaunchPending' -ErrorAction SilentlyContinue).UiRelaunchPending
    if (-not $value) { Write-Log 'no tray to bring back'; exit 0 }
    Remove-ItemProperty -Path $key -Name 'UiRelaunchPending' -ErrorAction SilentlyContinue
    $parts = $value -split ';', 2
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $age = if ($parts.Count -eq 2 -and $parts[0] -match '^[0-9]{1,12}$') { $now - [long]$parts[0] } else { -1 }
    if ($age -lt 0 -or $age -gt 900) { Write-Log "ignored request ($value): stale or invalid"; exit 0 }
    $session = (Get-Process -Id $PID).SessionId
    if (@($parts[1] -split ',') -notcontains [string]$session) { Write-Log "session $session not among $($parts[1])"; exit 0 }
    $reg = Get-ItemProperty -Path 'HKLM:\SOFTWARE\zaprett' -Name InstallDir -ErrorAction SilentlyContinue
    if ($null -eq $reg -or -not $reg.InstallDir) { Write-Log 'no installed zaprett'; exit 0 }
    $exe = Join-Path ([IO.Path]::GetFullPath($reg.InstallDir).TrimEnd('\')) 'zaprett-ui.exe'
    if (-not (Test-Path -LiteralPath $exe)) { Write-Log "$exe not found"; exit 0 }
    $how = Start-Unelevated $exe '--tray'
    Write-Log "tray started again in session $session ($how)"
}
catch { Write-Log "WARNING: $($_.Exception.Message)" }
exit 0
