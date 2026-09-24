namespace Zaprett.Ui.Core.Services;

/// <summary>The notification area as Shell_NotifyIcon sees it: one icon identified by our window and id 1.</summary>
public interface INotifyArea
{
    bool Add();

    bool SetVersion();

    bool Modify();

    bool Delete();
}

/// <summary>
/// Keeps exactly one tray icon per interface process. Shell_NotifyIcon identifies the icon by (window, id), so one
/// process can only duplicate it by adding under a second window; this class makes every path go through one
/// registration: the first add, updates, "TaskbarCreated" (Explorer restarted — the icon is gone — or the taskbar was
/// re-created while the icon still exists) and removal.
/// </summary>
public sealed class TrayRegistration(INotifyArea area, Action<string>? log = null)
{
    public bool IsAdded { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>First add; a second call does nothing.</summary>
    public void Create()
    {
        if (IsAdded || IsDisposed)
            return;
        AddFresh("added");
    }

    /// <summary>State, tooltip or balloon changed. A failed modify means the shell lost the icon: it is added again.</summary>
    public void Update()
    {
        if (IsDisposed || !IsAdded)
            return;
        if (!area.Modify())
        {
            log?.Invoke("tray: modify failed, adding the icon again");
            IsAdded = false;
            AddFresh("re-added after a failed modify");
        }
    }

    /// <summary>
    /// "TaskbarCreated": after an Explorer restart the icon is gone and has to be added; when only the taskbar was
    /// re-created it may still be there. Delete first (ignored if absent), then add: exactly one in both cases, and a
    /// failed NIM_ADD for an existing icon no longer leaves the interface believing it has none.
    /// </summary>
    public void OnTaskbarCreated()
    {
        if (IsDisposed)
            return;
        area.Delete();
        IsAdded = false;
        AddFresh("re-added after TaskbarCreated");
    }

    /// <summary>Exit: the icon goes away once.</summary>
    public void Dispose()
    {
        if (IsDisposed)
            return;
        if (IsAdded)
            area.Delete();
        IsAdded = false;
        IsDisposed = true;
    }

    private void AddFresh(string what)
    {
        IsAdded = area.Add();
        if (IsAdded)
        {
            area.SetVersion();
            log?.Invoke("tray: icon " + what);
        }
        else
            log?.Invoke("tray: Shell_NotifyIcon(NIM_ADD) failed");
    }
}

/// <summary>What a second start of the interface does with the running one.</summary>
public static class ActivationPolicy
{
    /// <summary>
    /// The Start menu shortcut (no arguments) brings the window; a start with --tray (Run key at sign-in, restart by
    /// the service after an update) or --minimized only makes sure the one running instance and its icon are there.
    /// </summary>
    public static bool ShowsWindow(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return true;
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return !args.Any(a => a.Trim('"') is "--tray" or "--minimized");
    }
}
