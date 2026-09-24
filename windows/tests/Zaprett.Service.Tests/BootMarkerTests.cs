using Microsoft.Win32;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>First service start since Windows started (main.autostart applies only then). The parent key here is a
/// scratch key under HKCU (deleted after each test), not HKLM\SOFTWARE\zaprett.</summary>
public sealed class BootMarkerTests : IDisposable
{
    private readonly string _root = @"Software\zaprett-tests\" + Guid.NewGuid().ToString("N");
    private readonly MemoryLog _log = new();

    private RegistryKey Parent() => Registry.CurrentUser.CreateSubKey(_root, writable: true);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        bool empty;
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\zaprett-tests"))
            empty = parent is { SubKeyCount: 0, ValueCount: 0 };
        if (empty)
            Registry.CurrentUser.DeleteSubKey(@"Software\zaprett-tests", throwOnMissingSubKey: false);
    }

    [Fact]
    public void FirstStartTrue_ThenFalse_UntilTheMarkIsGone()
    {
        Assert.True(new BootMarker(_log, Parent).FirstStartSinceBoot());
        // a service restart (a new process) in the same Windows session
        Assert.False(new BootMarker(_log, Parent).FirstStartSinceBoot());
        Assert.False(new BootMarker(_log, Parent).FirstStartSinceBoot());
        // what a reboot does to a volatile key
        using (var p = Parent())
            p.DeleteSubKey(BootMarker.KeyName);
        Assert.True(new BootMarker(_log, Parent).FirstStartSinceBoot());
    }

    // the mark is volatile: an ordinary key would survive the reboot and say "restart" forever
    [Fact]
    public void TheMark_IsVolatile()
    {
        new BootMarker(_log, Parent).FirstStartSinceBoot();
        using var p = Parent();
        // a non-volatile subkey cannot be created under a volatile key (ERROR_CHILD_MUST_BE_VOLATILE)
        using var mark = p.OpenSubKey(BootMarker.KeyName, writable: true)!;
        Assert.ThrowsAny<IOException>(() => mark.CreateSubKey("probe", RegistryKeyPermissionCheck.Default, RegistryOptions.None));
    }

    // negative control: no registry to read: "first start" (the behaviour before autostart and enabled were split)
    [Fact]
    public void NoParent_IsFirstStart()
    {
        Assert.True(new BootMarker(_log, () => null).FirstStartSinceBoot());
        Assert.True(new BootMarker(_log, () => throw new UnauthorizedAccessException("denied")).FirstStartSinceBoot());
        Assert.Contains(_log.Lines, l => l.Contains("taken as the first start"));
    }
}
