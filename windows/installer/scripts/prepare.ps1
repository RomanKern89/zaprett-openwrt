# Runs at the start of every install, repair and upgrade of the zaprett MSI (deferred custom action ZaprettPrepare
# as LocalSystem, right after InstallInitialize, before any file is replaced and before the old version is removed),
# so that no file of an installed zaprett is in use and no reboot is needed (1903 / 3010 on engine\WinDivert64.sys):
#   1. close the tray UI (zaprett-ui.exe from the install folder) of every signed-in user and record their sessions
#      in C:\ProgramData\zaprett\run\ui-relaunch.json, so that the service brings the tray back after the install;
#      the sessions whose tray the setup client already closed (close-ui.ps1, before InstallValidate) are added;
#   2. stop the zaprett service (it stops its winws / winws2, Job Object) and wait for it;
#   3. stop any winws.exe / winws2.exe still running from the install folder;
#   4. unload the WinDivert driver when its ImagePath points into the install folder (a foreign driver stays):
#      WinDivert deletes its own service on "sc stop" once nothing holds it (ARCHITECTURE-WIN S-8).
# The install folder comes from HKLM\SOFTWARE\zaprett\InstallDir (written by an installed version); nothing is
# installed yet on a first install, and the script does nothing. The MSI embeds this file as -EncodedCommand
# (build.ps1), so it must stay self-contained and ASCII. Every step is best effort; the exit code is always 0.
$ErrorActionPreference = 'Stop'
# no progress records: under -EncodedCommand they reach the MSI log as CLIXML
$ProgressPreference = 'SilentlyContinue'
# WixQuietExec decodes the output as OEM and writes OEM bytes into the ANSI-read MSI log: write ANSI bytes so that
# non-ASCII paths stay readable (the OEM round trip keeps them); characters outside the ANSI code page -> \uXXXX.
$script:LogOut = [Console]::OpenStandardOutput()
$script:Ansi = [Text.Encoding]::Default
function Write-Log([string]$Text) {
    $sb = New-Object Text.StringBuilder
    foreach ($ch in ("zaprett-prepare: $Text").ToCharArray()) {
        if ([int]$ch -lt 128 -or $script:Ansi.GetString($script:Ansi.GetBytes([string]$ch)) -eq [string]$ch) { [void]$sb.Append($ch) }
        else { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) }
    }
    $bytes = $script:Ansi.GetBytes($sb.ToString() + "`r`n")
    $script:LogOut.Write($bytes, 0, $bytes.Length)
    $script:LogOut.Flush()
}

function Test-UnderDir([string]$Path, [string]$Dir) {
    if (-not $Path) { return $false }
    $p = $Path.Trim().Trim('"')
    if ($p.StartsWith('\??\')) { $p = $p.Substring(4) }
    if ($p -match '^\\SystemRoot\\') { $p = Join-Path $env:SystemRoot $p.Substring(12) }
    try { $p = [IO.Path]::GetFullPath($p) } catch { return $false }
    return $p.StartsWith($Dir + '\', [StringComparison]::OrdinalIgnoreCase)
}

# Returns the SessionId of every process it stopped.
function Stop-OurProcesses([string[]]$Names, [string]$Dir) {
    $sessions = New-Object Collections.Generic.List[int]
    foreach ($p in @(Get-Process -Name $Names -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if (Test-UnderDir $path $Dir) {
            try {
                Stop-Process -Id $p.Id -Force
                Wait-Process -Id $p.Id -Timeout 10 -ErrorAction SilentlyContinue
                Write-Log "stopped $($p.ProcessName) (pid $($p.Id), session $($p.SessionId))"
                if (-not $sessions.Contains($p.SessionId)) { $sessions.Add($p.SessionId) }
            }
            catch { Write-Log "WARNING: could not stop $($p.ProcessName) (pid $($p.Id)): $($_.Exception.Message)" }
        }
    }
    return ,$sessions
}

# The service starts "zaprett-ui.exe --tray" again in these sessions when it starts after the install (format agreed
# with the service: {"version":1,"sessions":[1,3]}). Written only when a tray was closed and run\ exists; this
# script never runs for a real removal, so no stale file is left behind by an uninstall.
function Write-UiRelaunch([System.Collections.Generic.List[int]]$Sessions) {
    if ($null -eq $Sessions -or $Sessions.Count -eq 0) { return }
    $run = Join-Path $env:ProgramData 'zaprett\run'
    if (-not (Test-Path -LiteralPath $run)) { Write-Log 'no run folder, tray relaunch not recorded'; return }
    $json = '{"version":1,"sessions":[' + (($Sessions | Sort-Object) -join ',') + ']}'
    $target = Join-Path $run 'ui-relaunch.json'
    $tmp = "$target.tmp"
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $target -Force
    Write-Log "tray relaunch recorded for session(s) $(($Sessions | Sort-Object) -join ',')"
}

function Test-SessionOwner([int]$Session, [string]$Sid) {
    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe' AND SessionId=$Session" -ErrorAction SilentlyContinue)) {
        $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction SilentlyContinue
        if ($null -ne $owner -and $owner.Sid -eq $Sid) { return $true }
    }
    return $false
}

# Sessions whose tray the immediate ZaprettCloseUi (close-ui.ps1, as the installing user) closed before
# InstallValidate: HKU\<sid>\Software\zaprett\UiRelaunchPending = "<unix time>;<session>,...". Taken from every loaded
# user hive and deleted; only requests of the last 15 minutes and only sessions of that same user are kept.
function Get-PendingUiSessions {
    $result = New-Object Collections.Generic.List[int]
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    foreach ($hive in @(Get-ChildItem -Path 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '^S-1-5-21-[0-9-]+$' })) {
        $sid = $hive.PSChildName
        $key = "Registry::HKEY_USERS\$sid\Software\zaprett"
        $value = (Get-ItemProperty -LiteralPath $key -Name 'UiRelaunchPending' -ErrorAction SilentlyContinue).UiRelaunchPending
        if (-not $value) { continue }
        Remove-ItemProperty -LiteralPath $key -Name 'UiRelaunchPending' -ErrorAction SilentlyContinue
        $parts = $value -split ';', 2
        $age = if ($parts.Count -eq 2 -and $parts[0] -match '^[0-9]{1,12}$') { $now - [long]$parts[0] } else { -1 }
        if ($age -lt 0 -or $age -gt 900) { Write-Log "ignored tray relaunch request of $sid ($value): stale or invalid"; continue }
        foreach ($s in ($parts[1] -split ',')) {
            if ($s -notmatch '^[0-9]{1,5}$') { continue }
            if (-not (Test-SessionOwner ([int]$s) $sid)) { Write-Log "ignored session $s requested by ${sid}: not a session of that user"; continue }
            if (-not $result.Contains([int]$s)) { $result.Add([int]$s) }
            Write-Log "tray of session $s was closed by the setup client ($sid)"
        }
    }
    return ,$result
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $admin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Log "running as $($identity.Name), elevated: $admin"

    $reg = Get-ItemProperty -Path 'HKLM:\SOFTWARE\zaprett' -Name InstallDir -ErrorAction SilentlyContinue
    if ($null -eq $reg -or -not $reg.InstallDir) { Write-Log 'no installed zaprett, nothing to prepare'; exit 0 }
    $dir = [IO.Path]::GetFullPath($reg.InstallDir).TrimEnd('\')
    Write-Log "installed in $dir"

    $uiSessions = Stop-OurProcesses @('zaprett-ui') $dir
    foreach ($s in (Get-PendingUiSessions)) { if (-not $uiSessions.Contains($s)) { $uiSessions.Add($s) } }
    Write-UiRelaunch $uiSessions

    $svc = Get-Service -Name 'zaprett' -ErrorAction SilentlyContinue
    if ($null -ne $svc -and $svc.Status -ne 'Stopped') {
        try {
            if ($svc.Status -ne 'StopPending') { $svc.Stop() }
            # the service may take up to 40 s to stop (it ends its process by itself ~1.5 s after that)
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
            Write-Log 'stopped the zaprett service'
        }
        catch { Write-Log "WARNING: the zaprett service did not stop: $($_.Exception.Message)" }
    }
    # STOPPED is reported before the process has exited: wait up to 5 s for zaprett-svc.exe from our folder to go,
    # then end it, or InstallFiles finds the exe in use (3010)
    for ($i = 0; $i -lt 20; $i++) {
        $left = @(Get-Process -Name 'zaprett-svc' -ErrorAction SilentlyContinue |
                Where-Object { $path = $null; try { $path = $_.Path } catch { }; Test-UnderDir $path $dir })
        if ($left.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }
    [void](Stop-OurProcesses @('zaprett-svc') $dir)

    [void](Stop-OurProcesses @('winws', 'winws2') $dir)

    foreach ($key in @(Get-ChildItem -Path 'HKLM:\SYSTEM\CurrentControlSet\Services' -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -like 'WinDivert*' })) {
        $name = $key.PSChildName
        $image = (Get-ItemProperty -Path $key.PSPath -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
        if (-not (Test-UnderDir $image $dir)) { Write-Log "left driver service $name alone: not ours ($image)"; continue }
        & sc.exe stop $name | Out-Null
        for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $key.PSPath); $i++) { Start-Sleep -Milliseconds 250 }
        if (Test-Path -LiteralPath $key.PSPath) { Write-Log "WARNING: driver service $name ($image) is still registered" }
        else { Write-Log "unloaded driver $name ($image)" }
    }
}
catch { Write-Log "WARNING: $($_.Exception.Message)" }
exit 0
