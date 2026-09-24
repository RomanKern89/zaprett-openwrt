using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>D5-bis: the watchdog decides "not running" outside the engine lock while a start holds it (generation and a slow
/// dry-run). It must not start the engine a second time after that start, nor report a crash.</summary>
public sealed class WatchdogRaceTests
{
    /// <summary>Holds the next dry-run until released; tells when it is held.</summary>
    static (TaskCompletionSource Held, ManualResetEventSlim Release) SlowDryRun(Harness h)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var inner = h.F.Processes.Handler;
        h.F.Processes.Handler = (f, a) =>
        {
            if (a.Count > 0 && a[0] is "--dry-run" or "--intercept=0" && held.TrySetResult())
                release.Wait(TimeSpan.FromSeconds(30));
            return inner(f, a);
        };
        return (held, release);
    }

    static int Starts(Harness h) => h.F.Engine.Log.Count(l => l == "start main");

    static bool EnsureWarned(Harness h) =>
        h.F.Log.Lines.Any(l => l.StartsWith("W ", StringComparison.Ordinal) && (l.Contains("сторож", StringComparison.Ordinal) || l.Contains("watchdog", StringComparison.Ordinal)));

    [Fact]
    public async Task EnsureDuringAStart_DoesNotStartAgain_NorWarn()
    {
        using var h = new Harness();
        var (held, release) = SlowDryRun(h);
        var start = Task.Run(() => h.Call("start"));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(h.F.Engine.GetState("main").Running);
        var ensure = Task.Run(() => h.Call("ensure"));
        await Task.Delay(200);
        release.Set();
        var s = await start;
        var e = await ensure;
        Assert.True(R.IsOk(s), s.ToJsonString());
        Assert.True(R.IsOk(e), e.ToJsonString());
        Assert.Equal("none", R.Str(e["action"]));
        Assert.Equal(1, Starts(h));
        Assert.False(EnsureWarned(h));
    }

    [Fact]
    public async Task ACrashedEngine_IsStillStartedAndReported()
    {
        using var h = new Harness();
        await h.Call("start");
        h.F.Engine.Kill("main");
        var e = await h.Call("ensure");
        Assert.Equal("started", R.Str(e["action"]));
        Assert.Equal(2, Starts(h));
        Assert.True(EnsureWarned(h));
        Assert.True(h.F.Engine.GetState("main").Running);
    }
}
