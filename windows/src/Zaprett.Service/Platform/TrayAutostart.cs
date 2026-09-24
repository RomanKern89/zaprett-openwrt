using System.Security;
using Microsoft.Win32;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// "Show the icon at Windows sign-in": HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\zaprett =
/// "&lt;InstallDir&gt;zaprett-ui.exe" --tray, the value the installer's TRAYAUTOSTART writes. The service (SYSTEM) writes
/// and deletes it for the tray.autostart switch; the exe is always zaprett-ui.exe of this installation, never a path
/// from a request. The choice is kept in HKLM\SOFTWARE\zaprett\TrayAutostart (REG_DWORD 1/0) and applied again at every
/// service start, so an installer action that wrote or removed the Run value (a repair, the removal of the old product
/// during an update) does not undo it.
/// </summary>
public sealed class TrayAutostart(string uiExe, ILog log, Func<RegistryKey?>? openRun = null, Func<RegistryKey?>? openSettings = null)
{
    public const string RunValueName = "zaprett";
    public const string ChoiceValueName = "TrayAutostart";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKeyPath = @"SOFTWARE\zaprett";

    private readonly Func<RegistryKey?> _openRun = openRun ?? (() => Registry.LocalMachine.CreateSubKey(RunKeyPath, writable: true));
    private readonly Func<RegistryKey?> _openSettings = openSettings ?? (() => Registry.LocalMachine.CreateSubKey(SettingsKeyPath, writable: true));

    /// <summary>The Run value as the installer writes it.</summary>
    public string Command => $"\"{uiExe}\" --tray";

    /// <summary>Does the Run value exist and start our zaprett-ui.exe? Null when the registry cannot be read.</summary>
    public bool? IsOn()
    {
        try
        {
            using var run = _openRun();
            return run?.GetValue(RunValueName) is string cmd && IsOurs(cmd);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn($"tray autostart: cannot read the Run value: {e.Message}");
            return null;
        }
    }

    /// <summary>Writes or deletes the Run value and remembers the choice; the state read back afterwards.</summary>
    public bool Set(bool on)
    {
        using (var run = _openRun() ?? throw new IOException("the Run key cannot be opened"))
        {
            if (on)
                run.SetValue(RunValueName, Command, RegistryValueKind.String);
            else if (run.GetValue(RunValueName) is string cmd && IsOurs(cmd))
                run.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        using (var settings = _openSettings())
            settings?.SetValue(ChoiceValueName, on ? 1 : 0, RegistryValueKind.DWord);
        bool now = IsOn() == true;
        log.Info($"tray autostart: {(on ? "on" : "off")} (Run value {(now ? "present" : "absent")})");
        return now;
    }

    /// <summary>At the service start: the remembered choice, if there is one, is made true again.</summary>
    public void ApplyRemembered()
    {
        try
        {
            int? choice;
            using (var settings = _openSettings())
                choice = settings?.GetValue(ChoiceValueName) as int?;
            if (choice is not (0 or 1))
                return;
            bool want = choice == 1;
            if (IsOn() == want)
                return;
            log.Info($"tray autostart: the Run value did not match the saved choice ({(want ? "on" : "off")}), corrected");
            Set(want);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn($"tray autostart: cannot apply the saved choice: {e.Message}");
        }
    }

    /// <summary>A Run command starting exactly our exe ("…" quoted or up to the first space).</summary>
    private bool IsOurs(string cmd)
    {
        cmd = cmd.Trim();
        string exe = cmd.StartsWith('"')
            ? cmd[1..].Split('"', 2)[0]
            : cmd.Split(' ', 2)[0];
        try
        {
            return string.Equals(Path.GetFullPath(exe), Path.GetFullPath(uiExe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
