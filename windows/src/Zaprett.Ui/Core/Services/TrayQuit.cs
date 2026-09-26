namespace Zaprett.Ui.Core.Services;

/// <summary>What "Close zaprett" in the tray menu does in the current state.</summary>
public enum TrayQuitAction
{
    /// <summary>Nothing runs (bypass off, or no service to talk to): only the interface and its icon close.</summary>
    ExitOnly,

    /// <summary>The bypass runs and this user may change the service: turn the bypass off, then close.</summary>
    StopThenExit,

    /// <summary>The bypass runs but this user may only look (view-only): the item is greyed out, nothing happens.</summary>
    NotAllowed,
}

/// <summary>
/// "Close zaprett" of the tray menu closes zaprett completely: the bypass is turned off and the interface with its icon
/// goes away. The other item, "Exit the interface", keeps the bypass running in the service. A user without the right
/// to change the service cannot turn the bypass off, so for them the item is not offered as working.
/// </summary>
public static class TrayQuit
{
    public static TrayQuitAction Decide(bool available, bool running, bool canModify)
    {
        if (!available || !running) return TrayQuitAction.ExitOnly;
        return canModify ? TrayQuitAction.StopThenExit : TrayQuitAction.NotAllowed;
    }
}
