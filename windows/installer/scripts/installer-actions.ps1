<#
.SYNOPSIS
  Deferred custom actions of the zaprett MSI (runs as LocalSystem through WixQuietExec, output goes to the MSI log).

.DESCRIPTION
  -Action Install    create the local group "zaprett Operators" (ARCHITECTURE-WIN §6) and add the installing user
                     (its SID from the MSI property UserSID) to it; on a first install write config.json with
                     ui.language from -Language (LANG). Idempotent: repair and upgrade run it again.
  -Action Rollback   (rollback of a first install) delete the group again.
  -Action Uninstall  (real removal only, never on a major upgrade) stop winws.exe / winws2.exe started from our
                     folder, delete the WinDivert driver service only when its ImagePath points into our folder,
                     remove the zaprett firewall rules, delete the group, and with -RemoveData 1 delete
                     C:\ProgramData\zaprett. Every step is best effort: uninstall must not fail halfway.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Rollback', 'Uninstall')][string]$Action,
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [string]$UserSid = '',
    [string]$DataDir = '',
    [string]$RemoveData = '',
    [string]$Language = ''
)

$ErrorActionPreference = 'Stop'
$GroupName = 'zaprett Operators'
# New-LocalGroup allows at most 48 characters
$GroupDescription = 'May change zaprett settings and control it'
$FirewallRuleName = 'zaprett QUIC block'
$FirewallGroup = 'zaprett'
$EngineProcesses = @('winws', 'winws2')

function Write-Log([string]$Text) { Write-Output "zaprett-installer: $Text" }

# Full path without the trailing separator; '.' appended by the MSI (to keep a trailing '\' away from the quote) is removed here.
function Get-NormalPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-UnderDir([string]$Path, [string]$Dir) {
    if (-not $Path) { return $false }
    $p = $Path.Trim().Trim('"')
    if ($p.StartsWith('\??\')) { $p = $p.Substring(4) }
    if ($p -match '^\\SystemRoot\\') { $p = Join-Path $env:SystemRoot $p.Substring(12) }
    try { $p = [IO.Path]::GetFullPath($p) } catch { return $false }
    return $p.StartsWith($Dir + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    try { & $Body }
    catch { Write-Log "WARNING: $Name failed: $($_.Exception.Message)" }
}

function Install-Group {
    $group = Get-LocalGroup -Name $GroupName -ErrorAction SilentlyContinue
    if ($null -eq $group) {
        $group = New-LocalGroup -Name $GroupName -Description $GroupDescription
        Write-Log "created group '$GroupName' ($($group.SID))"
    }
    else {
        Write-Log "group '$GroupName' exists ($($group.SID))"
    }

    if (-not $UserSid -or $UserSid -eq 'S-1-5-18') {
        Write-Log 'no interactive installing user to add (UserSID empty or SYSTEM)'
        return
    }
    $sid = New-Object Security.Principal.SecurityIdentifier($UserSid)
    $isMember = @(Get-LocalGroupMember -Group $GroupName -ErrorAction SilentlyContinue | Where-Object { $_.SID -eq $sid }).Count -gt 0
    if ($isMember) {
        Write-Log "user $UserSid is already in '$GroupName'"
    }
    else {
        # Get-LocalGroupMember fails on groups holding orphaned SIDs, so "already a member" is not an error here
        try {
            # -Member takes the SID as a string (a SecurityIdentifier object does not bind to LocalPrincipal)
            Add-LocalGroupMember -Group $GroupName -Member $UserSid
            Write-Log "added user $UserSid to '$GroupName'"
        }
        catch [Microsoft.PowerShell.Commands.MemberExistsException] {
            Write-Log "user $UserSid is already in '$GroupName'"
        }
    }
}

function Get-InitialConfigText([string]$Lang) {
    return "{`n  `"schema`": 1,`n  `"ui`": { `"language`": `"$Lang`" }`n}`n"
}

# A first install writes ui.language (LANG: ru | en | zh-CN, ARCHITECTURE-WIN §12.2) into a NEW config.json; the
# service fills in every other key with its defaults. An existing config.json (upgrade, reinstall) is never touched.
function Write-InitialConfig([string]$Dir, [string]$Lang) {
    if (-not $Dir) { Write-Log 'no data directory given, config.json not written'; return }
    if ($Lang -notin @('ru', 'en', 'zh-CN')) {
        Write-Log "LANG '$Lang' is not ru, en or zh-CN; using ru"
        $Lang = 'ru'
    }
    $config = Join-Path $Dir 'config.json'
    if (Test-Path -LiteralPath $config) {
        Write-Log "config.json exists, language left as it is"
        return
    }
    [void](New-Item -ItemType Directory -Force -Path $Dir)
    [IO.File]::WriteAllText($config, (Get-InitialConfigText $Lang), (New-Object Text.UTF8Encoding($false)))
    Write-Log "wrote config.json with ui.language=$Lang"
}

function Stop-Engine([string]$Dir) {
    foreach ($p in @(Get-Process -Name $EngineProcesses -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if (Test-UnderDir $path $Dir) {
            Stop-Process -Id $p.Id -Force
            Wait-Process -Id $p.Id -Timeout 10 -ErrorAction SilentlyContinue
            Write-Log "stopped $($p.ProcessName) (pid $($p.Id))"
        }
        else {
            Write-Log "left $($p.ProcessName) (pid $($p.Id)) alone: not ours ($path)"
        }
    }
}

function Remove-OurWinDivert([string]$Dir) {
    $root = 'HKLM:\SYSTEM\CurrentControlSet\Services'
    foreach ($key in @(Get-ChildItem -Path $root -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'WinDivert*' })) {
        $name = $key.PSChildName
        $image = (Get-ItemProperty -Path $key.PSPath -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
        if (Test-UnderDir $image $Dir) {
            # WinDivert marks its own service for deletion: once no winws holds it, "sc stop" unloads the driver
            # and the service disappears without a reboot (SPIKE-M1 §8, ARCHITECTURE-WIN S-8)
            & sc.exe stop $name | Out-Null
            Start-Sleep -Milliseconds 500
            if (Test-Path -LiteralPath $key.PSPath) { & sc.exe delete $name | Out-Null }
            if (Test-Path -LiteralPath $key.PSPath) {
                Write-Log "WARNING: driver service $name ($image) is still registered; it goes away after a reboot"
            }
            else {
                Write-Log "unloaded and removed driver service $name ($image)"
            }
        }
        else {
            Write-Log "left driver service $name alone: ImagePath is not ours ($image)"
        }
    }
}

function Remove-FirewallRules {
    $rules = @(Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue) +
             @(Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue)
    $rules = @($rules | Sort-Object -Property Name -Unique)
    foreach ($r in $rules) {
        Remove-NetFirewallRule -Name $r.Name -ErrorAction SilentlyContinue
        Write-Log "removed firewall rule '$($r.DisplayName)'"
    }
    if ($rules.Count -eq 0) { Write-Log 'no zaprett firewall rules' }
}

function Remove-Group {
    if (Get-LocalGroup -Name $GroupName -ErrorAction SilentlyContinue) {
        Remove-LocalGroup -Name $GroupName
        Write-Log "deleted group '$GroupName'"
    }
}

function Remove-Data([string]$Dir) {
    $programData = Get-NormalPath $env:ProgramData
    if (-not $Dir -or $Dir -ne (Join-Path $programData 'zaprett')) {
        Write-Log "WARNING: refusing to delete data directory '$Dir' (expected $programData\zaprett)"
        return
    }
    if (Test-Path -LiteralPath $Dir) {
        Remove-Item -LiteralPath $Dir -Recurse -Force
        Write-Log "deleted $Dir"
    }
}

$dir = Get-NormalPath $InstallDir
# the install folder may be chosen freely (ARCHITECTURE-WIN §12.1), so it is recognised by our service binary
if (-not (Test-Path -LiteralPath (Join-Path $dir 'zaprett-svc.exe'))) {
    Write-Log "ERROR: '$dir' does not look like a zaprett install directory (no zaprett-svc.exe)"
    exit 1
}
Write-Log "action $Action, install directory $dir"

if ($Action -eq 'Rollback') {
    # a failed first install: the group created by -Action Install goes away with it, and so does config.json if
    # it is still exactly the one Write-InitialConfig wrote (a config.json kept from an earlier install stays)
    Invoke-Step 'group' { Remove-Group }
    Invoke-Step 'config.json' {
        $config = Join-Path (Get-NormalPath $DataDir) 'config.json'
        if (Test-Path -LiteralPath $config) {
            $text = [IO.File]::ReadAllText($config)
            if (@('ru', 'en', 'zh-CN' | Where-Object { (Get-InitialConfigText $_) -ceq $text }).Count -gt 0) {
                Remove-Item -LiteralPath $config -Force
                Write-Log "removed the config.json written by this install"
            }
        }
    }
    exit 0
}

if ($Action -eq 'Install') {
    try { Install-Group; Write-InitialConfig (Get-NormalPath $DataDir) $Language }
    catch { Write-Log "ERROR: $($_.Exception.Message)"; exit 1 }
    exit 0
}

Invoke-Step 'stop engine' { Stop-Engine $dir }
Invoke-Step 'WinDivert service' { Remove-OurWinDivert $dir }
Invoke-Step 'firewall rules' { Remove-FirewallRules }
Invoke-Step 'group' { Remove-Group }
if ($RemoveData -eq '1') {
    Invoke-Step 'data directory' { Remove-Data (Get-NormalPath $DataDir) }
}
else {
    Write-Log 'data directory kept (REMOVEDATA is not 1)'
}
exit 0
