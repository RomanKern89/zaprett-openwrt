using System.Text.Json.Nodes;

namespace Zaprett.Service.Hosting;

/// <summary>The core's start at service start (CommandDispatcher.StartupAsync): rollback of a lost automatic selection
/// and the engine when enabled. The stub dispatcher has none.</summary>
public sealed record CoreStartup(Func<CancellationToken, Task<JsonObject>> Run);

/// <summary>
/// Opens when the service start of the core is over (successful or not). The scheduler waits for it: a watchdog
/// ("ensure") running while the core is still starting the engine would find it not running, start it a second time
/// (stop + start, a WinDivert restart) and log a false "engine was not running" (the Windows 11 test machine, 2026-09-23).
/// </summary>
public sealed class StartupGate
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Done => _done.Task;

    public void Open() => _done.TrySetResult();
}
