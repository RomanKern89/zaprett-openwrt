using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Store;
using Zaprett.Core.Strategy;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Changes while the engine runs (found on the Windows 11 test machine): winws refuses a second copy with the same WinDivert filter
/// even for --dry-run, so the argument check must not trip over the running engine, and a saved change must reach it.</summary>
public sealed class RunningEngineTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    [Fact]
    public async Task FakeWinws_RefusesPlainDryRun_WhileItRuns()
    {
        // negative control of the fake itself: the plain --dry-run of the running arguments hits the duplicate check
        using var h = new Harness();
        await h.Call("start");
        var args = h.F.Engine.ArgsOf("main")!;
        var r = await h.F.Processes.RunAsync(Path.Combine(h.F.Paths.EngineDir, "winws.exe"), ["--dry-run", .. args], TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("already running with the same filter", r.StdErr);
    }

    [Fact]
    public async Task Restart_StrategySet_WhileRunning()
    {
        using var h = new Harness();
        Assert.True(R.IsOk(await h.Call("start")));
        var r = await h.Call("restart");
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.Equal(2, h.F.Engine.Log.Count(l => l == "start main"));
        var s = await h.Call("strategy.set", A(("id", "strategy-alt")));
        Assert.True(R.IsOk(s), s.ToJsonString());
        Assert.True(R.Bool(s["reloaded"]));
        Assert.Null(s["reload_error"]);
        Assert.Equal(3, h.F.Engine.Log.Count(l => l == "start main"));
        Assert.Empty(Directory.GetFiles(h.F.Paths.RunDir, "dryrun-filter-*"));
    }

    [Theory]
    [InlineData("debug", true)]
    [InlineData("ipv6", true)]
    [InlineData("game_filter", true)]
    [InlineData("list_mode", "blacklist")]
    [InlineData("game_ports_tcp", "443")]
    public async Task SettingsThatChangeArguments_RestartTheEngine(string key, object value)
    {
        using var h = new Harness();
        await h.Call("start");
        await h.Call("list.enable", A(("id", "zaprett-telegram-ipset")));
        if (key == "game_ports_tcp")
            await h.Call("settings.set", A(("main", new JsonObject { ["game_filter"] = true })));
        var before = h.F.Engine.ArgsOf("main")!.ToList();
        var main = new JsonObject { [key] = JsonValue.Create(value) };
        var r = await h.Call("settings.set", A(("main", main)));
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.True(R.Bool(r["reloaded"]), r.ToJsonString());
        Assert.NotEqual(before, h.F.Engine.ArgsOf("main"));
        if (key == "debug")
            Assert.Equal("--debug=1", h.F.Engine.ArgsOf("main")![0]);
    }

    [Fact]
    public async Task SettingsThatDoNotChangeArguments_DoNotRestart()
    {
        using var h = new Harness();
        await h.Call("start");
        var r = await h.Call("settings.set", A(("monitor", new JsonObject { ["interval"] = 15 })));
        Assert.True(R.IsOk(r));
        Assert.False(R.Bool(r["reloaded"]));
        Assert.Equal(1, h.F.Engine.Log.Count(l => l == "start main"));
    }

    [Fact]
    public async Task FailedRestart_IsReported_AndTheOldEngineKeepsRunning()
    {
        using var h = new Harness();
        await h.Call("start");
        var before = h.F.Engine.ArgsOf("main")!.ToList();
        // the engine rejects the debug arguments (as a stand-in for any refusal)
        h.F.Processes.Handler = (f, a) => a.Any(x => x.StartsWith("--wf-l3=ipv4,ipv6", StringComparison.Ordinal))
            ? new ProcessResult(1, "", "bad option", false) : new ProcessResult(0, "", "", false);
        // a change the engine refuses is not saved at all (router validate_change), the engine keeps its arguments
        var r = await h.Call("settings.set", A(("main", new JsonObject { ["ipv6"] = true })));
        Assert.Equal("dry_run_failed", R.Error(r));
        Assert.False(h.D.Context.Config.Load().Ipv6);
        Assert.Equal(before, h.F.Engine.ArgsOf("main"));
        // accepted by the check but the engine does not come up: saved, and the failure is reported, not hidden
        h.F.Processes.Handler = (_, _) => new ProcessResult(0, "", "", false);
        h.F.Engine.FailWhen = (_, a) => a.Contains("--wf-l3=ipv4,ipv6");
        var r2 = await h.Call("settings.set", A(("main", new JsonObject { ["ipv6"] = true })));
        Assert.True(R.IsOk(r2), r2.ToJsonString());
        Assert.False(R.Bool(r2["reloaded"]));
        Assert.Equal("engine_not_running", R.Str(r2["reload_error"]!["code"]));
    }

    sealed class SilentZero : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new ProcessResult(0, "", "", false));
    }

    [Fact]
    public async Task DryRun_WithoutSavedFilter_IsNotAPass()
    {
        // code 0 without the filter file: winws stopped before building the filter — must not count as checked
        using var fp = new FakePlatform();
        var gen = new ArgsGenerator(fp.Paths, new ItemStore(fp.Paths));
        Assert.Equal(1, (await gen.DryRunAsync(new SilentZero(), "winws", ["--wf-tcp=443"], CancellationToken.None)).Rc);
        Assert.Equal(0, (await gen.DryRunAsync(new SilentZero(), "winws2", ["--wf-tcp-out=443"], CancellationToken.None)).Rc);
    }
}
