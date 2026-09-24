<#
.SYNOPSIS
  Starts zaprett-ui.exe NOT elevated from an elevated setup (finish page of the MSI, "Start zaprett").

.DESCRIPTION
  When msiexec itself runs elevated (admin console, "Run as administrator"), a process it starts would be elevated
  too. The UI must run with the user's normal token, so it is started by a one-shot scheduled task of the same
  user with LogonType Interactive and RunLevel Limited (the filtered, non-elevated token) in the user's session;
  the task is removed right after. Nothing here may fail the installation: every error is logged, exit code 0.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Arguments = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$name = 'zaprett-launch-ui-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
try {
    if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "zaprett-launch: $Exe not found"; exit 0 }
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ($Arguments) { $action = New-ScheduledTaskAction -Execute $Exe -Argument $Arguments -WorkingDirectory (Split-Path -Parent $Exe) }
    else { $action = New-ScheduledTaskAction -Execute $Exe -WorkingDirectory (Split-Path -Parent $Exe) }
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    $procName = [IO.Path]::GetFileNameWithoutExtension($Exe)
    for ($i = 0; $i -lt 20; $i++) {
        if ((Get-ScheduledTask -TaskName $name).State -eq 'Running') { break }
        if (Get-Process -Name $procName -ErrorAction SilentlyContinue) { break }
        Start-Sleep -Milliseconds 250
    }
    Write-Output "zaprett-launch: started $Exe as $user (not elevated)"
}
catch {
    Write-Output "zaprett-launch: WARNING: could not start the UI: $($_.Exception.Message)"
}
finally {
    # the running process does not depend on the task any more
    Start-Sleep -Seconds 2
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}
exit 0
