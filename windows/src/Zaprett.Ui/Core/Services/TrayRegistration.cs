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

    /// <summary>Create was called: from now on a missing icon is added again (a failed first add is retried).</summary>
    public bool IsCreated { get; private set; }

    /// <summary>First add; a second call does nothing.</summary>
    public void Create()
    {
        if (IsAdded || IsDisposed)
            return;
        IsCreated = true;
        AddFresh("added");
    }

    /// <summary>
    /// A first add that failed (the shell was not ready yet: right after sign-in, or while Explorer is busy with a
    /// starting setup) is tried again: on every update of the state and by the retry timer of the icon.
    /// </summary>
    public void Retry()
    {
        if (IsDisposed || !IsCreated || IsAdded)
            return;
        AddFresh("added on retry");
    }

    /// <summary>State, tooltip or balloon changed. A failed modify means the shell lost the icon: it is added again.</summary>
    public void Update()
    {
        if (IsDisposed || !IsCreated)
            return;
        if (!IsAdded)
        {
            Retry();
            return;
        }
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
        // NIM_ADD may report a failure (a time-out) although the shell did add the icon: then it answers a modify,
        // and it is used instead of adding a second one
        if (!IsAdded && area.Modify())
        {
            IsAdded = true;
            area.SetVersion();
            log?.Invoke("tray: icon " + what + " (it was there already)");
            return;
        }
        if (IsAdded)
        {
            area.SetVersion();
            log?.Invoke("tray: icon " + what);
        }
        else
            log?.Invoke("tray: Shell_NotifyIcon(NIM_ADD) failed");
    }
}

/// <summary>
/// The handle of the tray image for a state. An image that could not be loaded (LoadImage gave NULL) must never go
/// to Shell_NotifyIcon: an icon without an image is kept by the shell but not shown (Windows 10, 2026-09-24: added
/// in the state "off", invisible until the first modify). Then the image of another state is used.
/// </summary>
public static class TrayImages
{
    public static readonly string[] States = ["on", "off", "warn", "error"];

    public static IntPtr Pick(string state, IReadOnlyDictionary<string, IntPtr> loaded)
    {
        if (loaded.TryGetValue(state, out var h) && h != IntPtr.Zero)
            return h;
        foreach (var s in new[] { "off", "on", "warn", "error" })
        {
            if (loaded.TryGetValue(s, out var other) && other != IntPtr.Zero)
                return other;
        }
        return IntPtr.Zero;
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
