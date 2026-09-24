using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Jobs;
using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Background jobs: the job manager itself, the automatic selection, probe, monitor, diagnosis.</summary>
public sealed class JobTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    [Fact]
    public async Task JobManager_OneAtATime_ProgressLogCancel()
    {
        using var p = new TestPaths();
        var jm = new JobManager(p, new FakeClock());
        var gate = new TaskCompletionSource();
        var started = jm.Start("probe", async ctx =>
        {
            ctx.Progress(40, "шаг");
            ctx.Partial(new JsonObject { ["tested"] = 1 });
            await gate.Task.WaitAsync(ctx.Token);
            return R.Ok();
        });
        Assert.True(R.IsOk(started));
        Assert.Equal("job_busy", R.Error(jm.Start("test", _ => Task.FromResult(R.Ok()))));
        Assert.Equal("job_busy", R.Error(await jm.RunForegroundAsync("test", _ => Task.FromResult(R.Ok()))));
        var cancel = jm.Cancel();
        Assert.Equal("cancelling", R.Str(cancel["state"]));
        await jm.WhenIdleAsync();
        var j = jm.Read()!;
        Assert.Equal("cancelled", R.Str(j["state"]));
        Assert.Equal(1L, R.Long(j["rc"]));
        Assert.Contains("шаг", jm.LogTail(100));
        Assert.Contains("Итог: cancelled", jm.LogTail(1));
        Assert.Equal("no_job", R.Error(jm.Cancel()));

        var fg = await jm.RunForegroundAsync("repo-fetch", ctx => Task.FromResult(R.Ok(new JsonObject { ["x"] = 1 })));
        Assert.True(R.IsOk(fg));
        Assert.Equal("done", R.Str(jm.Read()!["state"]));
        Assert.Equal(100L, R.Long(jm.Read()!["progress"]));

        jm.Start("probe", _ => throw new InvalidOperationException("boom"));
        await jm.WhenIdleAsync();
        Assert.Equal("failed", R.Str(jm.Read()!["state"]));
        Assert.Equal("internal_error", R.Error((JsonObject)jm.Read()!["result"]!));

        jm.Start("probe", _ => Task.FromResult(R.Fail("x", "плохо")));
        await jm.WhenIdleAsync();
        Assert.Equal("failed", R.Str(jm.Read()!["state"]));
        Assert.Equal("плохо", R.Str(jm.Read()!["message"]));
    }

    [Fact]
    public void JobManager_MarksLostJobFailed_AndTrimsLog()
    {
        using var p = new TestPaths();
        Directory.CreateDirectory(p.RunDir);
        File.WriteAllText(Path.Combine(p.RunDir, "job.json"), "{\"id\":\"1-1\",\"name\":\"test\",\"state\":\"running\"}");
        var jm = new JobManager(p, new FakeClock());
        Assert.Equal("failed", R.Str(jm.Read()!["state"]));
        var line = new string('x', 1000);
        for (var i = 0; i < 400; i++)
            jm.AppendLog(line);
        Assert.True(new FileInfo(jm.LogPath).Length <= JobManager.LogLimit);
        Assert.StartsWith("[…журнал обрезан…]", File.ReadAllText(jm.LogPath, Encoding.UTF8));
        Assert.Equal(3, jm.LogTail(3).Split('\n').Length);
    }

    /// <summary>A target is reachable only through a running instance whose argv carries the marker of the good strategy.</summary>
    static void GoodStrategyOpensSites(Harness h, string marker)
    {
        h.F.Http.Probe = req =>
        {
            var inst = req.LocalPortFrom != null ? "test" : "main";
            var args = h.F.Engine.ArgsOf(inst);
            var ok = args != null && args.Contains(marker);
            return new ProbeResult(req.Url, ok, 30, ok ? 500000 : 0, ok ? null : "reset", null);
        };
    }

    static string UniqueToken(string good, string bad)
    {
        var dir = Path.Combine(RepoPaths.BundleDir, "files", "strategies", "nfqws");
        var g = Strategy.ArgsGenerator.Tokenize(File.ReadAllText(Path.Combine(dir, good + ".txt"))).Tokens;
        var b = Strategy.ArgsGenerator.Tokenize(File.ReadAllText(Path.Combine(dir, bad + ".txt"))).Tokens;
        return g.First(t => !t.Contains("${", StringComparison.Ordinal) && t != "--new" && !b.Contains(t));
    }

    [Fact]
    public async Task Test_RunsDespiteABlockingConflict_AndFlagsTheResults()
    {
        using var h = new Harness();
        await h.Call("start");
        var clean = (JsonObject)(await h.Job("test.start", A(("strategies", new JsonArray("strategy-general")))))["result"]!;
        Assert.False(R.Bool(clean["conflict_blocking"]));
        Assert.Empty((JsonArray)clean["conflicts_blocking"]!);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        var j = await h.Job("test.start", A(("strategies", new JsonArray("strategy-general"))));
        Assert.Equal("done", R.Str(j["state"]));
        var res = (JsonObject)j["result"]!;
        Assert.True(R.Bool(res["conflict_blocking"]));
        Assert.Equal("zapret", R.Str(Assert.Single((JsonArray)res["conflicts_blocking"]!)!["id"]));
        Assert.Single((JsonArray)(await h.Call("test.status"))["results"]!["conflicts_blocking"]!);
        Assert.Contains(h.F.Log.Lines, l => l.StartsWith("W ", StringComparison.Ordinal) && l.Contains("zapret / winws", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_Exclusive_RanksRestoresAndApplies()
    {
        using var h = new Harness();
        await h.Call("start");
        GoodStrategyOpensSites(h, UniqueToken("strategy-alt", "strategy-general"));
        var j = await h.Job("test.start", A(("strategies", new JsonArray("strategy-general", "strategy-alt")), ("apply_if_better", true)));
        Assert.Equal("done", R.Str(j["state"]));
        var res = (JsonObject)j["result"]!;
        Assert.Equal("exclusive", R.Str(res["mode"]));
        Assert.Equal("not_supported", R.Str(res["mode_reason"]));
        Assert.Equal("strategy-alt", R.Str(res["best"]));
        Assert.Equal("strategy-alt", R.Str(res["applied"]));
        Assert.Equal(0L, R.Long(res["baseline_ok"]));
        Assert.Equal("strategy-alt", h.D.Context.Config.Load().Strategy);
        Assert.True(h.F.Engine.GetState("main").Running);
        Assert.False(File.Exists(Path.Combine(h.F.Paths.RunDir, "test-state.json")));
        Assert.Contains("stop main", h.F.Engine.Log);
        var st = await h.Call("test.status", A(("brief", true)));
        Assert.False(R.Bool(st["running"]));
        var results = (JsonArray)st["results"]!["results"]!;
        Assert.Equal("strategy-alt", R.Str(results[0]!["id"]));
        Assert.Equal(1.0, (double)R.Number(results[0]!["ratio"])!);
        Assert.Null(results[0]!["targets"]);
        Assert.Equal("done", R.Str(st["results"]!["state"]));
        var full = await h.Call("test.status");
        Assert.NotNull(full["results"]!["results"]![0]!["targets"]);
    }

    [Fact]
    public async Task Test_Isolated_KeepsMainRunning()
    {
        using var h = new Harness(new CoreOptions { IsolationSupported = true });
        await h.Call("start");
        var mainArgs = h.F.Engine.ArgsOf("main")!.ToList();
        GoodStrategyOpensSites(h, UniqueToken("strategy-alt", "strategy-general"));
        var j = await h.Job("test.start", A(("strategies", new JsonArray("strategy-general", "strategy-alt"))));
        var res = (JsonObject)j["result"]!;
        Assert.Equal("isolated", R.Str(res["mode"]));
        Assert.Null(res["applied"]);
        Assert.Equal("strategy-alt", R.Str(res["best"]));
        Assert.DoesNotContain("stop main", h.F.Engine.Log);
        Assert.Equal(mainArgs, h.F.Engine.ArgsOf("main"));
        Assert.False(h.F.Engine.GetState("test").Running);
        Assert.All(h.F.Http.Requests, r => Assert.Equal(40000, r.LocalPortFrom));
        Assert.Equal("strategy-general", h.D.Context.Config.Load().Strategy);
    }

    [Fact]
    public async Task Test_Isolated_FallsBackWhenInstanceFails()
    {
        using var h = new Harness(new CoreOptions { IsolationSupported = true });
        await h.Call("start");
        h.F.Engine.FailWhen = (inst, _) => inst == "test";
        var j = await h.Job("test.start", A(("strategies", new JsonArray("strategy-general"))));
        var res = (JsonObject)j["result"]!;
        Assert.Equal("exclusive", R.Str(res["mode"]));
        Assert.Equal("instance_failed", R.Str(res["mode_reason"]));
        Assert.True(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task Test_ErrorsStopAndApply()
    {
        using var h = new Harness();
        var bad = await h.Job("test.start", A(("strategies", new JsonArray("nope"))));
        Assert.Equal("strategy_not_found", R.Error((JsonObject)bad["result"]!));
        await h.Call("settings.set", A(("main", new JsonObject { ["lists"] = new JsonArray() })));
        var none = await h.Job("test.start", A(("quick", true)));
        Assert.Equal("no_targets", R.Error((JsonObject)none["result"]!));
        Assert.Equal("no_job", R.Error(await h.Call("test.stop")));
        File.WriteAllText(Path.Combine(h.F.Paths.RunDir, "test-state.json"), "{\"mode\":\"exclusive\",\"was_running\":false}");
        Assert.Equal("test_running", R.Error(await h.Call("test.apply", A(("id", "strategy-alt")))));
        var stop = await h.Call("test.stop");
        Assert.Equal("restored", R.Str(stop["state"]));
        Assert.False(File.Exists(Path.Combine(h.F.Paths.RunDir, "test-state.json")));
        var ap = await h.Call("test.apply", A(("id", "strategy-alt")));
        Assert.True(R.IsOk(ap), ap.ToJsonString());
        Assert.Equal("strategy-alt", h.D.Context.Config.Load().Strategy);
        Assert.Equal("strategy_not_found", R.Error(await h.Call("test.apply", A(("id", "z2-general")))));
        File.Delete(Path.Combine(h.F.Paths.EngineDir, "winws.exe"));
        var miss = await h.Job("test.start");
        Assert.Equal("engine_missing", R.Error((JsonObject)miss["result"]!));
    }

    [Fact]
    public async Task Test_InvalidCandidateAndEngineFailure()
    {
        using var h = new Harness();
        h.F.Processes.Handler = (f, a) => a.Contains("--version") || !a.Contains(UniqueToken("strategy-alt", "strategy-general"))
            ? new ProcessResult(0, "", "", false) : new ProcessResult(1, "", "rejected", false);
        h.F.Engine.FailWhen = (inst, a) => a.Contains(UniqueToken("strategy-alt2", "strategy-general"));
        var j = await h.Job("test.start", A(("strategies", new JsonArray("strategy-general", "strategy-alt", "strategy-alt2")), ("foreground", false)));
        var list = (JsonArray)(await h.Call("test.status"))["results"]!["results"]!;
        string Status(string id) => R.Str(list.OfType<JsonObject>().First(r => R.Str(r["id"]) == id)["status"])!;
        Assert.Equal("done", Status("strategy-general"));
        Assert.Equal("invalid", Status("strategy-alt"));
        Assert.Equal("engine_failed", Status("strategy-alt2"));
        Assert.Equal("done", R.Str(j["state"]));
        Assert.False(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task Test_CancelRestoresEngine()
    {
        using var h = new Harness();
        await h.Call("start");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.F.Http.Probe = req =>
        {
            entered.TrySetResult();
            release.Task.Wait(5000);
            return new ProbeResult(req.Url, false, 1, 0, "timeout", null);
        };
        await h.Call("test.start", A(("strategies", new JsonArray("strategy-general", "strategy-alt"))));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelling", R.Str((await h.Call("test.stop"))["state"]));
        release.SetResult();
        await h.D.Context.Jobs.WhenIdleAsync();
        Assert.Equal("cancelled", R.Str(h.D.Context.Jobs.Read()!["state"]));
        Assert.True(h.F.Engine.GetState("main").Running);
        Assert.False(File.Exists(Path.Combine(h.F.Paths.RunDir, "test-state.json")));
    }

    [Fact]
    public async Task Restore_SurvivesPlatformErrors_AndRespectsDisable()
    {
        using var h = new Harness();
        await h.Call("start");
        var state = Path.Combine(h.F.Paths.RunDir, "test-state.json");
        // a failing stop of instance "test" must not leave the state behind (the watchdog would be blocked forever)
        File.WriteAllText(state, "{\"mode\":\"exclusive\",\"was_running\":true}");
        h.F.Engine.ThrowOnStop.Add("test");
        var r = await h.Call("test.stop");
        Assert.Equal("restored", R.Str(r["state"]));
        Assert.False(R.Bool(r["reloaded"]));
        Assert.False(File.Exists(state));
        Assert.Contains(h.F.Log.Lines, l => l.Contains("не удалось вернуть", StringComparison.Ordinal));
        h.F.Engine.ThrowOnStop.Clear();
        // disabled during the selection: the engine is not started again
        await h.Call("settings.set", A(("main", new JsonObject { ["enabled"] = false })));
        File.WriteAllText(state, "{\"mode\":\"exclusive\",\"was_running\":true}");
        await h.Call("test.stop");
        Assert.False(h.F.Engine.GetState("main").Running);
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task Probe_PerService()
    {
        using var h = new Harness();
        h.F.Http.Probe = r => !r.Url.Contains("discord", StringComparison.Ordinal)
            ? new ProbeResult(r.Url, true, 40, 999999, null, 200)
            : new ProbeResult(r.Url, false, 40, 0, "tls_error", null);
        var j = await h.Job("probe");
        Assert.Equal("done", R.Str(j["state"]));
        var p = (await h.Call("probe.status"))["probe"]!;
        var svc = ((JsonArray)p["services"]!).OfType<JsonObject>().ToDictionary(s => R.Str(s["id"])!);
        Assert.Equal(svc["youtube"]["total"]!.GetValue<int>(), (int)R.Long(svc["youtube"]["ok"])!);
        Assert.Equal(0L, R.Long(svc["discord"]["ok"]));
        Assert.Equal("tls_error", R.Str(svc["discord"]["targets"]![0]!["error"]));
        Assert.Contains(h.Events, e => e.Type == "probe");
        Assert.Contains(h.Events, e => e.Type == "job");
        var bad = await h.Job("probe", A(("services", new JsonArray("nope"))));
        Assert.Equal("unknown_service", R.Error((JsonObject)bad["result"]!));
        var fg = await h.Call("probe", A(("services", "youtube"), ("foreground", true)));
        Assert.True(R.IsOk(fg), fg.ToJsonString());
    }

    [Fact]
    public async Task Monitor_DegradesAndStartsRepair()
    {
        using var h = new Harness();
        await h.Call("settings.set", A(("monitor", new JsonObject { ["threshold"] = 2, ["auto_repair"] = true })));
        Assert.Equal("service_disabled", R.Str((await h.Call("monitor.run"))["skipped"]));
        await h.Call("start");
        h.F.Http.Probe = r => new ProbeResult(r.Url, false, 1, 0, "timeout", null);
        var r1 = await h.Call("monitor.run");
        Assert.Equal("ok", R.Str(r1["state"]));
        Assert.Equal(0L, R.Long(r1["reachable"]));
        var r2 = await h.Call("monitor.run");
        Assert.Equal("repairing", R.Str(r2["state"]));
        Assert.True(R.Bool(r2["repair_started"]));
        Assert.Contains("monitor_degraded", R.Strings((await h.Call("status"))["warnings"]));
        await h.D.Context.Jobs.WhenIdleAsync();
        var job = h.D.Context.Jobs.Read()!;
        Assert.Equal("test", R.Str(job["name"]));
        var m = (await h.Call("monitor.status"))["monitor"]!;
        Assert.Equal("degraded", R.Str(m["state"]));
        Assert.Equal(2, ((JsonArray)m["history"]!).Count);
        Assert.Contains(h.Events, e => e.Type == "monitor");
        await h.Call("stop");
        Assert.Equal("stopped", R.Str((await h.Call("monitor.run"))["skipped"]));
    }

    [Fact]
    public async Task Diagnose_Verdicts()
    {
        using var h = new Harness();
        h.F.Dns.System["www.youtube.com"] = ["10.0.0.1"];
        h.F.Dns.Doh["www.youtube.com"] = ["142.250.1.1"];
        h.F.Dns.System["discord.com"] = ["162.159.1.1"];
        h.F.Dns.Doh["discord.com"] = ["162.159.1.1"];
        h.F.Dns.System["gateway.discord.gg"] = ["162.159.2.2"];
        h.F.Dns.Doh["gateway.discord.gg"] = null;
        h.F.Dns.Tcp = (ip, port) => false;
        h.F.Http.Probe = r => r.Url.Contains("discord", StringComparison.Ordinal)
            ? new ProbeResult(r.Url, false, 1, 0, "timeout", null)
            : new ProbeResult(r.Url, true, 1, 999999, null, 200);
        var j = await h.Job("diagnose", A(("services", new JsonArray("youtube", "discord"))));
        Assert.Equal("done", R.Str(j["state"]));
        var d = (await h.Call("diagnose.status"))["diagnose"]!;
        var byUrl = ((JsonArray)d["targets"]!).OfType<JsonObject>().ToDictionary(t => R.Str(t["url"])!);
        Assert.Equal("dns_spoof", R.Str(byUrl["https://www.youtube.com/"]["verdict"]));
        Assert.Equal("ip_block", R.Str(byUrl.First(kv => kv.Key.StartsWith("https://discord.com", StringComparison.Ordinal)).Value["verdict"]));
        Assert.False(R.Bool(byUrl.First(kv => kv.Key.StartsWith("https://discord.com", StringComparison.Ordinal)).Value["tcp"]));
        Assert.NotNull(d["summary"]!["verdict"]);
        var none = await h.Job("diagnose", A(("services", new JsonArray("whatsapp"))));
        Assert.Equal("no_targets", R.Error((JsonObject)none["result"]!));
    }
}
