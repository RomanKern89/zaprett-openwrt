using System.Security;
using Microsoft.Win32;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// Is this the first service start since Windows started? main.autostart decides the engine only then (a restart of
/// the service by an update, a repair or the recovery actions keeps what the user had, ARCHITECTURE-WIN §5). Told by a
/// volatile registry key, HKLM\SOFTWARE\zaprett\Boot (REG_OPTION_VOLATILE): the kernel drops it at every reboot, so it
/// is absent exactly at the first start of a Windows session. A fresh install counts as a first start. When the key
/// cannot be read or created the answer is "first start" (the behaviour before the split).
/// </summary>
public sealed class BootMarker(ILog log, Func<RegistryKey?>? openParent = null)
{
    public const string KeyName = "Boot";
    private const string ParentPath = @"SOFTWARE\zaprett";

    private readonly Func<RegistryKey?> _openParent = openParent ?? (() => Registry.LocalMachine.CreateSubKey(ParentPath, writable: true));

    /// <summary>True at the first call in a Windows session (and marks it); false afterwards until the next reboot.</summary>
    public bool FirstStartSinceBoot()
    {
        try
        {
            using var parent = _openParent();
            if (parent is null)
                return true;
            using (var existing = parent.OpenSubKey(KeyName))
            {
                if (existing is not null)
                    return false;
            }
            using (parent.CreateSubKey(KeyName, RegistryKeyPermissionCheck.Default, RegistryOptions.Volatile))
                return true;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn($"boot marker: {e.Message}; taken as the first start since Windows started");
            return true;
        }
    }
}
