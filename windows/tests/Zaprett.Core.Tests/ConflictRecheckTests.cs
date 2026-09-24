using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>D22: while another bypass program blocks the traffic, failed monitor checks do not count towards degraded; when
/// it goes away, one extra check brings the monitor state back to reality at once, not at the next planned check.</summary>
public sealed class ConflictRecheckTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    static void SitesDown(Harness h) => h.F.Http.Probe = r => new ProbeResult(r.Url, false, 1, 0, "timeout", null);

    static void SitesUp(Harness h) => h.F.Http.Probe = r => new ProbeResult(r.Url, true, 50, 200000, null, 200);

    static int Rechecks(Harness h) => h.F.Log.Lines.Count(l =>
        l.Contains("внеочередная", StringComparison.Ordinal) || l.Contains("extra availability check", StringComparison.Ordinal));

    static async Task<JsonObject> Monitor(Harness h) => (JsonObject)(await h.Call("monitor.status"))["monitor"]!;

    static async Task<Harness> RunningAsync(int threshold, bool autoRepair)
    {
        var h = new Harness();
        // the service's startup gives the core its lifetime: background checks only after it
        await h.D.StartupAsync(false, CancellationToken.None);
        await h.Call("settings.set", A(("monitor", new JsonObject { ["threshold"] = threshold, ["auto_repair"] = autoRepair })));
        await h.Call("start");
        return h;
    }

    [Fact]
    public async Task FailuresDuringAConflict_DoNotLeadToDegraded_NorToARepair()
    {
        using var h = await RunningAsync(3, true);
        SitesDown(h);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        for (var i = 0; i < 5; i++)
        {
            var r = await h.Call("monitor.run");
            Assert.Equal("ok", R.Str(r["state"]));
            Assert.Equal(0L, R.Long(r["consecutive_failures"]));
            Assert.False(R.Bool(r["repair_started"]));
        }
        Assert.Null(h.D.Context.Jobs.Read());
        var m = await Monitor(h);
        Assert.True(R.Bool(m["conflict_blocking"]));
        var history = (JsonArray)m["history"]!;
        Assert.Equal(5, history.Count);
        Assert.All(history, e => Assert.True(R.Bool(e!["conflict"])));
        // control: the same failures without the conflict do reach degraded
        h.F.Conflicts.Items.Clear();
        for (var i = 0; i < 3; i++)
            await h.Call("monitor.run");
        // auto_repair is on: degraded starts the repair at once, so the state is "repairing"
        Assert.Contains(R.Str((await Monitor(h))["state"]), new[] { "degraded", "repairing" });
        Assert.Equal(3L, R.Long((await Monitor(h))["consecutive_failures"]));
    }

    [Fact]
    public async Task FailuresBeforeTheConflict_AreKept_NotIncreased()
    {
        using var h = await RunningAsync(3, false);
        SitesDown(h);
        await h.Call("monitor.run");
        await h.Call("monitor.run");
        Assert.Equal(2L, R.Long((await Monitor(h))["consecutive_failures"]));
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("monitor.run");
        await h.Call("monitor.run");
        var m = await Monitor(h);
        Assert.Equal(2L, R.Long(m["consecutive_failures"]));
        Assert.Equal("ok", R.Str(m["state"]));
    }

    [Fact]
    public async Task AlreadyDegraded_NoRepairStartsDuringTheConflict()
    {
        using var h = await RunningAsync(2, false);
        SitesDown(h);
        await h.Call("monitor.run");
        await h.Call("monitor.run");
        Assert.Equal("degraded", R.Str((await Monitor(h))["state"]));
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("settings.set", A(("monitor", new JsonObject { ["auto_repair"] = true })));
        var r = await h.Call("monitor.run");
        Assert.False(R.Bool(r["repair_started"]), r.ToJsonString());
        Assert.Equal("conflict_blocking", R.Str(r["repair_blocked"]));
        Assert.Null(h.D.Context.Jobs.Read());
        // control: without the conflict the same state starts the repair
        h.F.Conflicts.Items.Clear();
        Assert.True(R.Bool((await h.Call("monitor.run"))["repair_started"]));
    }

    [Fact]
    public async Task ConflictGone_OneExtraCheck_BringsTheStateBackToOk()
    {
        using var h = await RunningAsync(2, false);
        SitesDown(h);
        await h.Call("monitor.run");
        await h.Call("monitor.run");
        Assert.Equal("degraded", R.Str((await Monitor(h))["state"]));
        // no conflict ever seen: status does not start extra checks
        await h.Call("status");
        Assert.Null(h.D.LastMonitorRecheck);
        // the other program appears (status sees it), then is closed and the sites open again
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        Assert.Null(h.D.LastMonitorRecheck);
        h.F.Conflicts.Items.Clear();
        SitesUp(h);
        var events = h.Events.Count(e => e.Type == "monitor");
        await h.Call("status");
        Assert.NotNull(h.D.LastMonitorRecheck);
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        var m = await Monitor(h);
        Assert.Equal("ok", R.Str(m["state"]));
        Assert.Equal(0L, R.Long(m["consecutive_failures"]));
        Assert.Equal("ok", R.Str((await h.Call("status"))["monitor"]!["state"]));
        Assert.Equal(1, Rechecks(h));
        Assert.Equal(events + 1, h.Events.Count(e => e.Type == "monitor"));
        // further status calls: nothing more
        var last = h.D.LastMonitorRecheck;
        for (var i = 0; i < 5; i++)
            await h.Call("status");
        Assert.Same(last, h.D.LastMonitorRecheck);
        Assert.Equal(1, Rechecks(h));
    }

    [Fact]
    public async Task AFlappingConflict_GivesOneExtraCheckPerInterval_NotAStorm()
    {
        using var h = await RunningAsync(2, false);
        SitesUp(h);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        h.F.Conflicts.Items.Clear();
        await h.Call("status");
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Rechecks(h));
        for (var i = 0; i < 10; i++)
        {
            h.F.Conflicts.Items.Add(FakeConflicts.Winws());
            await h.Call("status");
            h.F.Conflicts.Items.Clear();
            await h.Call("status");
        }
        Assert.Equal(1, Rechecks(h));
        // after the interval the last change is still checked once
        h.F.Clock.Now += TimeSpan.FromSeconds(Checks.Health.RecheckInterval + 1);
        await h.Call("status");
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, Rechecks(h));
        h.F.Clock.Now += TimeSpan.FromSeconds(Checks.Health.RecheckInterval + 1);
        await h.Call("status");
        Assert.Equal(2, Rechecks(h));
    }

    [Fact]
    public async Task TheWatchdog_NoticesItToo_EvenAfterAServiceRestart()
    {
        using var h = await RunningAsync(2, false);
        SitesDown(h);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("monitor.run");
        Assert.True(R.Bool((await Monitor(h))["conflict_blocking"]));
        // the service restarts (no window open), the other program is gone
        var d = new CommandDispatcher(h.F.Services);
        await d.StartupAsync(false, CancellationToken.None);
        h.F.Conflicts.Items.Clear();
        SitesUp(h);
        await d.InvokeAsync("ensure", null, CallerInfo.System, CancellationToken.None);
        Assert.NotNull(d.LastMonitorRecheck);
        await d.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        var m = (JsonObject)(await d.InvokeAsync("monitor.status", null, CallerInfo.System, CancellationToken.None))["monitor"]!;
        Assert.False(R.Bool(m["conflict_blocking"]));
        Assert.Equal("ok", R.Str(m["state"]));
    }

    [Fact]
    public async Task AFailedScan_IsNotAConflictThatWentAway()
    {
        using var h = await RunningAsync(2, false);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        h.F.Conflicts.Fail = true;
        await h.Call("status");
        await h.Call("ensure");
        Assert.Null(h.D.LastMonitorRecheck);
        Assert.Equal(0, Rechecks(h));
    }

    [Fact]
    public async Task TheExtraCheck_AndAPlannedCheck_NeverOverlap()
    {
        using var h = await RunningAsync(2, false);
        await h.Call("settings.set", A(("monitor", new JsonObject { ["max_targets"] = 1 })));
        var gate = new ManualResetEventSlim(false);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maxActive = 0, calls = 0;
        h.F.Http.Probe = r =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            if (Interlocked.Increment(ref calls) == 1)
            {
                first.TrySetResult();
                gate.Wait(TimeSpan.FromSeconds(30));
            }
            Interlocked.Decrement(ref active);
            return new ProbeResult(r.Url, true, 50, 200000, null, 200);
        };
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        h.F.Conflicts.Items.Clear();
        await h.Call("status");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var planned = Task.Run(() => h.Call("monitor.run"));
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.False(planned.IsCompleted);
        gate.Set();
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        var r = await planned.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.Equal(2, calls);
        Assert.Equal(1, maxActive);
    }

    /* ---------- review of D22: M1, M2, L1-L5 ---------- */

    static async Task<Harness> DegradedAsync()
    {
        var h = await RunningAsync(2, false);
        SitesDown(h);
        await h.Call("monitor.run");
        await h.Call("monitor.run");
        Assert.Equal("degraded", R.Str((await Monitor(h))["state"]));
        return h;
    }

    [Fact]
    public async Task M1_ACheckSkippedBecauseTheEngineIsDown_StaysDue_AndRunsOnceItIsUp()
    {
        using var h = await DegradedAsync();
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        // the other program took our engine down with it; it is gone now and the sites would open
        h.F.Engine.Kill("main");
        h.F.Conflicts.Items.Clear();
        SitesUp(h);
        await h.Call("status");
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("degraded", R.Str((await Monitor(h))["state"]));
        Assert.Equal(0, Rechecks(h));
        var skipped = h.D.LastMonitorRecheck;
        // the watchdog brings the engine back; the retry is not due yet
        Assert.Equal("started", R.Str((await h.Call("ensure"))["action"]));
        Assert.Same(skipped, h.D.LastMonitorRecheck);
        // after the short retry (not the whole interval) the next status runs it
        h.F.Clock.Now += TimeSpan.FromSeconds(Checks.Health.RecheckRetry + 1);
        await h.Call("status");
        Assert.NotSame(skipped, h.D.LastMonitorRecheck);
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("ok", R.Str((await Monitor(h))["state"]));
        Assert.Equal(1, Rechecks(h));
    }

    [Fact]
    public async Task M2_AProgramClosedDuringTheProbes_SpoilsNoCount_AndACheckFollows()
    {
        using var h = await RunningAsync(1, true);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        // the probes fail because of the other program, which closes while they run
        h.F.Http.Probe = r =>
        {
            lock (h.F.Conflicts.Items)
                h.F.Conflicts.Items.Clear();
            return new ProbeResult(r.Url, false, 1, 0, "timeout", null);
        };
        var r = await h.Call("monitor.run");
        Assert.Equal("ok", R.Str(r["state"]));
        Assert.Equal(0L, R.Long(r["consecutive_failures"]));
        Assert.False(R.Bool(r["repair_started"]));
        Assert.Null(h.D.Context.Jobs.Read());
        // the spoiled check is followed by a real one
        SitesUp(h);
        await h.Call("status");
        Assert.NotNull(h.D.LastMonitorRecheck);
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Rechecks(h));
        Assert.Equal("ok", R.Str((await Monitor(h))["state"]));
    }

    static bool PutOffLogged(Harness h) => h.F.Log.Lines.Any(l => l.StartsWith("W ", StringComparison.Ordinal) &&
        (l.Contains("отложен", StringComparison.Ordinal) || l.Contains("put off", StringComparison.Ordinal)));

    [Fact]
    public async Task MNew_AScannerBrokenForGood_WithoutAnyConflict_DoesNotSwitchTheRepairOff()
    {
        using var h = await RunningAsync(1, true);
        SitesDown(h);
        h.F.Conflicts.Fail = true;
        var r = await h.Call("monitor.run");
        Assert.True(R.Bool(r["repair_started"]), r.ToJsonString());
        Assert.Null(r["repair_blocked"]);
        Assert.False(PutOffLogged(h));
    }

    [Fact]
    public async Task MNew_UnknownAfterASeenConflict_PutsTheRepairOff_ForAFewChecks_Visibly()
    {
        using var h = await RunningAsync(1, true);
        SitesDown(h);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("monitor.run");
        Assert.True(R.Bool((await Monitor(h))["conflict_blocking"]));
        // the scanner breaks while the program may still be there
        h.F.Conflicts.Fail = true;
        for (var i = 1; i <= Checks.Health.UnknownRepairLimit; i++)
        {
            var r = await h.Call("monitor.run");
            Assert.False(R.Bool(r["repair_started"]), $"check {i}: {r.ToJsonString()}");
            Assert.Equal("conflict_unknown", R.Str(r["repair_blocked"]));
            Assert.Equal("conflict_unknown", R.Str((await Monitor(h))["repair_blocked"]));
            Assert.Equal("conflict_unknown", R.Str((await h.Call("status"))["monitor"]!["repair_blocked"]));
        }
        Assert.Null(h.D.Context.Jobs.Read());
        Assert.True(PutOffLogged(h));
        // not for ever: after the limit the monitor repairs as before
        var last = await h.Call("monitor.run");
        Assert.True(R.Bool(last["repair_started"]), last.ToJsonString());
        Assert.Null(last["repair_blocked"]);
    }

    [Fact]
    public async Task La_AFailedExtraCheck_WaitsTheWholeInterval_NotTheShortRetry()
    {
        using var h = await DegradedAsync();
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        h.F.Conflicts.Items.Clear();
        // the extra check fails: its state cannot be written (a directory stands where monitor.json goes)
        var monitorFile = Path.Combine(h.F.Paths.RunDir, "monitor.json");
        var saved = monitorFile + ".saved";
        File.Move(monitorFile, saved);
        Directory.CreateDirectory(monitorFile);
        try
        {
            SitesUp(h);
            await h.Call("status");
            await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Directory.Delete(monitorFile);
            File.Move(saved, monitorFile);
        }
        Assert.Equal("degraded", R.Str((await Monitor(h))["state"]));
        var failed = h.D.LastMonitorRecheck;
        h.F.Clock.Now += TimeSpan.FromSeconds(Checks.Health.RecheckRetry + 1);
        await h.Call("status");
        Assert.Same(failed, h.D.LastMonitorRecheck);
        // still due: after the whole interval it runs again
        h.F.Clock.Now += TimeSpan.FromSeconds(Checks.Health.RecheckInterval);
        await h.Call("status");
        Assert.NotSame(failed, h.D.LastMonitorRecheck);
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("ok", R.Str((await Monitor(h))["state"]));
    }

    [Fact]
    public async Task Le_BeforeTheServiceStartup_TheChangeIsOnlyRemembered()
    {
        using var h = new Harness();
        await h.Call("settings.set", A(("monitor", new JsonObject { ["threshold"] = 2, ["auto_repair"] = false })));
        await h.Call("start");
        SitesDown(h);
        await h.Call("monitor.run");
        await h.Call("monitor.run");
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        h.F.Conflicts.Items.Clear();
        SitesUp(h);
        await h.Call("status");
        Assert.Null(h.D.LastMonitorRecheck);
        // after the startup the remembered change is checked
        await h.D.StartupAsync(false, CancellationToken.None);
        await h.Call("status");
        Assert.NotNull(h.D.LastMonitorRecheck);
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("ok", R.Str((await Monitor(h))["state"]));
    }

    [Fact]
    public async Task L1_NoExtraCheckAfterTheServiceStops()
    {
        using var h = await DegradedAsync();
        using var life = new CancellationTokenSource();
        await h.D.StartupAsync(false, life.Token);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        life.Cancel();
        h.F.Conflicts.Items.Clear();
        await h.Call("status");
        Assert.Null(h.D.LastMonitorRecheck);
        // control: with a live lifetime the same change starts it
        using var h2 = await DegradedAsync();
        using var life2 = new CancellationTokenSource();
        await h2.D.StartupAsync(false, life2.Token);
        h2.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h2.Call("status");
        h2.F.Conflicts.Items.Clear();
        await h2.Call("status");
        Assert.NotNull(h2.D.LastMonitorRecheck);
    }

    [Fact]
    public async Task L2_NoRecheckLine_WhenTheCheckDoesNotRun()
    {
        using var h = await DegradedAsync();
        await h.Call("settings.set", A(("monitor", new JsonObject { ["enabled"] = false })));
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status");
        h.F.Conflicts.Items.Clear();
        await h.Call("status");
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, Rechecks(h));
        // switched off is not a passing reason: not retried
        var last = h.D.LastMonitorRecheck;
        h.F.Clock.Now += TimeSpan.FromSeconds(Checks.Health.RecheckRetry + 1);
        await h.Call("status");
        Assert.Same(last, h.D.LastMonitorRecheck);
    }

    [Fact]
    public async Task AScannerThatKeepsFailing_IsReportedOncePerSeries()
    {
        using var h = new Harness();
        static int Warns(Harness x) => x.F.Log.Lines.Count(l => l.StartsWith("W ", StringComparison.Ordinal) && l.Contains("scan failed", StringComparison.Ordinal));
        h.F.Conflicts.Fail = true;
        await h.Call("status");
        await h.Call("ensure");
        await h.Call("status");
        Assert.Equal(1, Warns(h));
        h.F.Conflicts.Fail = false;
        await h.Call("status");
        h.F.Conflicts.Fail = true;
        await h.Call("status");
        await h.Call("status");
        Assert.Equal(2, Warns(h));
    }

    [Fact]
    public async Task L5_TheBackgroundCheck_SpeaksTheServiceLanguage()
    {
        using var h = await DegradedAsync();
        await h.Call("settings.set", A(("ui", new JsonObject { ["language"] = "en" })));
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        await h.Call("status", A(("lang", "ru")));
        h.F.Conflicts.Items.Clear();
        SitesUp(h);
        await h.Call("status", A(("lang", "ru")));
        await h.D.LastMonitorRecheck!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(h.F.Log.Lines, l => l.Contains("extra availability check", StringComparison.Ordinal));
        Assert.DoesNotContain(h.F.Log.Lines, l => l.Contains("внеочередная", StringComparison.Ordinal));
    }

    static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while (value > (cur = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, cur) != cur)
        {
        }
    }
}
