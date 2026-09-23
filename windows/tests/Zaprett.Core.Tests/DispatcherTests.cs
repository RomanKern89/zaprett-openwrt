using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

public sealed class DispatcherTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    [Fact]
    public async Task Access_ReadOnlyForUsers_ModifyingDenied()
    {
        using var h = new Harness();
        var denied = await h.Call("start", null, Harness.User);
        Assert.Equal("access_denied", R.Error(denied));
        Assert.False(h.F.Engine.GetState("main").Running);
        Assert.True(R.IsOk(await h.Call("status", null, Harness.User)));
        Assert.True(R.IsOk(await h.Call("items", null, Harness.User)));
        Assert.Equal("access_denied", R.Error(await h.Call("settings.set", A(("main", new JsonObject { ["enabled"] = true })), Harness.User)));
        Assert.Equal("access_denied", R.Error(await h.Call("ensure", null, Harness.User)));
        Assert.Equal("unknown_method", R.Error(await h.Call("fw.apply")));
        Assert.Equal("bad_args", R.Error(await h.Call("strategy.set")));
        Assert.Equal("bad_args", R.Error(await h.Call("strategy.set", A(("id", 5)))));
        Assert.Contains("status", h.D.ReadOnlyMethods);
        Assert.DoesNotContain("start", h.D.ReadOnlyMethods);
        // every method of ARCHITECTURE-WIN §6 is known
        foreach (var m in new[] { "status", "start", "stop", "restart", "enable", "disable", "check", "items", "list.enable", "list.disable",
                     "strategy.set", "strategy.show", "strategy.save", "strategy.delete", "user.get", "user.set", "mode", "engine", "repo.fetch",
                     "repo.list", "repo.install", "repo.remove", "repo.upgrade", "sources.list", "sources.update", "sources.save", "sources.delete",
                     "presets", "wizard.apply", "test.start", "test.status", "test.stop", "test.apply", "job.status", "job.log", "job.cancel",
                     "probe", "probe.status", "monitor.status", "diagnose", "diagnose.status", "dns.status", "dns.setup", "log", "diag", "version",
                     "page", "conflicts", "settings.get", "settings.set", "update.check", "update.install" })
            Assert.Contains(m, h.D.Methods);
    }

    [Fact]
    public async Task Status_Fresh()
    {
        using var h = new Harness();
        var s = await h.Call("status");
        Assert.True(R.IsOk(s));
        Assert.False(R.Bool(s["running"]));
        Assert.Equal("winws", R.Str(s["engine"]));
        Assert.Equal("v72.13", R.Str(s["engine_version"]));
        Assert.Equal("strategy-general", R.Str(s["strategy"]!["id"]));
        Assert.Equal("Windows 11", R.Str(s["platform"]!["os"]));
        Assert.True(R.Bool(s["windivert"]!["loaded"]));
        Assert.Empty(R.Strings(s["warnings"]));
        Assert.Null(s["job"]);
        Assert.Equal("unknown", R.Str(s["monitor"]!["state"]));
    }

    [Fact]
    public async Task Status_Warnings()
    {
        using var h = new Harness(engines: false);
        await h.Call("settings.set", A(("main", new JsonObject { ["strategy"] = "nope", ["lists"] = new JsonArray("gone-list", "src-x") })));
        File.WriteAllText(h.F.Paths.ConfigFile, File.ReadAllText(h.F.Paths.ConfigFile).Replace("\"whitelist\"", "\"gray\"", StringComparison.Ordinal));
        await h.Call("enable");
        var w = R.Strings((await h.Call("status"))["warnings"]);
        foreach (var x in new[] { "bad_config", "engine_missing", "strategy_missing", "list_missing", "not_running" })
            Assert.Contains(x, w);
        Assert.All(w, x => Assert.Contains(x, Warnings.All));
    }

    [Fact]
    public async Task StartStopRestart_EnableDisable()
    {
        using var h = new Harness();
        var r = await h.Call("start");
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.True(h.F.Engine.GetState("main").Running);
        var args = h.F.Engine.ArgsOf("main")!;
        Assert.StartsWith("--wf-l3=ipv4", args[0]);
        Assert.Contains(h.F.Processes.Calls, c => c.Args[0] == "--dry-run");
        Assert.True(h.D.Context.Config.Load().Enabled);
        var st = await h.Call("status");
        Assert.True(R.Bool(st["running"]));
        Assert.NotNull(st["engine_stats"]);
        Assert.True(File.Exists(Path.Combine(h.F.Paths.RunDir, "args")));

        Assert.True(R.IsOk(await h.Call("stop")));
        Assert.False(h.F.Engine.GetState("main").Running);
        Assert.True(h.D.Context.UserStopped);
        Assert.Equal("none", R.Str((await h.Call("ensure"))["action"]));
        Assert.True(R.IsOk(await h.Call("restart")));
        Assert.True(h.F.Engine.GetState("main").Running);
        Assert.False(h.D.Context.UserStopped);

        Assert.True(R.IsOk(await h.Call("disable")));
        Assert.False(h.F.Engine.GetState("main").Running);
        Assert.Equal("disabled", R.Error(await h.Call("restart")));
        Assert.True(R.IsOk(await h.Call("enable")));
        Assert.False(h.F.Engine.GetState("main").Running);
        Assert.Contains(h.Events, e => e.Type == "status");
    }

    [Fact]
    public async Task Start_DryRunFailure_IsReported()
    {
        using var h = new Harness();
        h.F.Processes.Handler = (f, a) => a.Contains("--version") ? new ProcessResult(0, "v", "", false) : new ProcessResult(2, "", "unknown option", false);
        var r = await h.Call("start");
        Assert.Equal("dry_run_failed", R.Error(r));
        Assert.False(h.F.Engine.GetState("main").Running);
        Assert.Contains("generate_failed", R.Strings((await h.Call("status"))["warnings"]));
        Assert.Equal("dry_run_failed", R.Error(await h.Call("check")));
        h.F.Engine.FailWhen = (_, _) => true;
        h.F.Processes.Handler = (_, _) => new ProcessResult(0, "", "", false);
        Assert.Equal("engine_not_running", R.Error(await h.Call("start")));
    }

    [Fact]
    public async Task Check_GivesArgsAndPorts()
    {
        using var h = new Harness();
        var r = await h.Call("check");
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.Contains("443", R.Strings(r["ports"]!["tcp"]));
        Assert.Equal(0L, R.Long(r["dry_run"]!["rc"]));
        Assert.Equal("strategy-general", R.Str(r["strategy"]!["id"]));
    }

    [Fact]
    public async Task QuicBlock_FollowsConfigAndEngine()
    {
        using var h = new Harness();
        await h.Call("start");
        Assert.False(h.F.Firewall.Blocked);
        var s = await h.Call("settings.set", A(("main", new JsonObject { ["quic_block"] = true })));
        Assert.True(R.IsOk(s), s.ToJsonString());
        Assert.True(h.F.Firewall.Blocked);
        h.F.Firewall.Blocked = false;
        Assert.Equal("fw_applied", R.Str((await h.Call("ensure"))["action"]));
        Assert.True(h.F.Firewall.Blocked);
        await h.Call("stop");
        Assert.False(h.F.Firewall.Blocked);
    }

    [Fact]
    public async Task Ensure_RestartsDeadEngine_SkipsDuringTest()
    {
        using var h = new Harness();
        await h.Call("start");
        h.F.Engine.Kill("main");
        var r = await h.Call("ensure");
        Assert.Equal("started", R.Str(r["action"]));
        Assert.Equal("not_running", R.Str(r["reason"]));
        Assert.True(h.F.Engine.GetState("main").Running);
        File.WriteAllText(Path.Combine(h.F.Paths.RunDir, "test-state.json"), "{}");
        Assert.Equal("skipped", R.Str((await h.Call("ensure"))["action"]));
        Assert.Equal("test_running", R.Error(await h.Call("start")));
        Assert.Equal("test_running", R.Error(await h.Call("stop")));
        h.F.Engine.FailWhen = (_, _) => true;
        File.Delete(Path.Combine(h.F.Paths.RunDir, "test-state.json"));
        h.F.Engine.Kill("main");
        Assert.Equal("engine_not_running", R.Error(await h.Call("ensure")));
    }

    [Fact]
    public async Task ListToggle_ReloadsRunningEngine()
    {
        using var h = new Harness();
        await h.Call("start");
        var before = h.F.Engine.ArgsOf("main")!.ToList();
        var r = await h.Call("list.enable", A(("id", "zaprett-telegram")));
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.True(R.Bool(r["reloaded"]));
        Assert.Contains("zaprett-telegram", h.D.Context.Config.Load().Lists);
        Assert.NotEqual(before, h.F.Engine.ArgsOf("main"));
        Assert.False(R.Bool((await h.Call("list.enable", A(("id", "zaprett-telegram"))))["changed"]));
        Assert.True(R.IsOk(await h.Call("list.disable", A(("id", "zaprett-telegram")))));
        Assert.DoesNotContain("zaprett-telegram", h.D.Context.Config.Load().Lists);
        Assert.Equal("not_found", R.Error(await h.Call("list.enable", A(("id", "nope")))));
        Assert.Equal("bad_id", R.Error(await h.Call("list.enable", A(("id", "bad id")))));
        // a subscription can be switched on before its first download
        var src = await h.Call("list.enable", A(("id", "src-refilter_domains")));
        Assert.True(R.IsOk(src), src.ToJsonString());
        Assert.Contains("source_not_downloaded", R.Strings(src["warnings"]));
    }

    [Fact]
    public async Task Mode_RefusedWhenItBreaksAWorkingConfig()
    {
        using var h = new Harness();
        // the fake engine rejects any argv without an include hostlist: blacklist mode then fails the dry-run
        h.F.Processes.Handler = (f, a) => a.Contains("--version") || a.Any(x => x.StartsWith("--hostlist=", StringComparison.Ordinal))
            ? new ProcessResult(0, "", "", false) : new ProcessResult(1, "", "no hostlist", false);
        var r = await h.Call("mode", A(("mode", "blacklist")));
        Assert.Equal("dry_run_failed", R.Error(r));
        Assert.Equal("whitelist", h.D.Context.Config.Load().ListMode);
        Assert.Equal("bad_value", R.Error(await h.Call("mode", A(("mode", "gray")))));
        h.F.Processes.Handler = (_, _) => new ProcessResult(0, "", "", false);
        Assert.True(R.IsOk(await h.Call("mode", A(("mode", "blacklist")))));
        Assert.Equal("blacklist", h.D.Context.Config.Load().ListMode);
        // both configurations failing: the change is allowed with config_was_invalid
        h.F.Processes.Handler = (_, _) => new ProcessResult(1, "", "broken", false);
        var both = await h.Call("mode", A(("mode", "whitelist")));
        Assert.True(R.IsOk(both), both.ToJsonString());
        Assert.Contains("config_was_invalid", R.Strings(both["warnings"]));
    }

    [Fact]
    public async Task Strategies_SetShowSaveDelete()
    {
        using var h = new Harness();
        Assert.Equal("strategy_not_found", R.Error(await h.Call("strategy.set", A(("id", "nope")))));
        var set = await h.Call("strategy.set", A(("id", "strategy-alt")));
        Assert.True(R.IsOk(set), set.ToJsonString());
        Assert.Equal("strategy-alt", h.D.Context.Config.Load().Strategy);
        var show = await h.Call("strategy.show", A(("id", "z2-general")));
        Assert.Equal("nfqws2", R.Str(show["type"]));
        Assert.Contains(R.Strings(show["args"]), a => a.StartsWith("--lua-init=", StringComparison.Ordinal));
        Assert.Equal("bad_id", R.Error(await h.Call("strategy.save", A(("id", "mine"), ("text", "--x")))));
        Assert.Equal("unknown_placeholder", R.Error(await h.Call("strategy.save", A(("id", "user-bad"), ("text", "--filter-tcp=443 --dpi-desync-fake-tls=${zzz}")))));
        Assert.Equal("too_large", R.Error(await h.Call("strategy.save", A(("id", "user-big"), ("text", new string('a', 70000))))));
        var save = await h.Call("strategy.save", A(("id", "user-mine"), ("text", "--filter-tcp=443 ${hostlists} --dpi-desync=fake")));
        Assert.True(R.IsOk(save), save.ToJsonString());
        Assert.True(File.Exists(Path.Combine(h.F.Paths.UserDir, "strategies", "winws", "user-mine.txt")));
        Assert.True(R.IsOk(await h.Call("strategy.set", A(("id", "user-mine")))));
        Assert.Equal("item_active", R.Error(await h.Call("strategy.delete", A(("id", "user-mine")))));
        await h.Call("strategy.set", A(("id", "strategy-general")));
        Assert.True(R.IsOk(await h.Call("strategy.delete", A(("id", "user-mine")))));
        Assert.Equal("not_found", R.Error(await h.Call("strategy.delete", A(("id", "user-mine")))));
        Assert.Equal("bad_id", R.Error(await h.Call("strategy.delete", A(("id", "strategy-general")))));
        Assert.Equal("strategy_not_found", R.Error(await h.Call("strategy.show", A(("id", "nope")))));
    }

    [Fact]
    public async Task UserLists()
    {
        using var h = new Harness();
        await h.Call("start");
        Assert.Equal("bad_id", R.Error(await h.Call("user.get", A(("id", "zaprett-youtube")))));
        var bad = await h.Call("user.set", A(("id", "user-hosts"), ("text", "ok.com\nbad domain\n")));
        Assert.Equal("invalid_entries", R.Error(bad));
        Assert.Equal(2L, R.Long(bad["errors"]![0]!["line"]));
        var ok = await h.Call("user.set", A(("id", "user-hosts"), ("text", "Example.com\n")));
        Assert.True(R.IsOk(ok), ok.ToJsonString());
        Assert.Equal(1L, R.Long(ok["entries"]));
        var get = await h.Call("user.get", A(("id", "user-hosts")));
        Assert.Equal("example.com\n", R.Str(get["text"]));
        Assert.Equal("too_large", R.Error(await h.Call("user.set", A(("id", "user-ipset"), ("text", new string('1', 1100000))))));
        Assert.Equal("too_large", R.Error(await h.Call("user.set", A(("id", "user-ipset")))));
    }

    [Fact]
    public async Task Engine_SwitchTakesPresetDefault()
    {
        using var h = new Harness();
        var r = await h.Call("engine", A(("engine", "winws2")));
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.Equal("z2-general", R.Str(r["strategy"]));
        var cfg = h.D.Context.Config.Load();
        Assert.Equal("winws2", cfg.Engine);
        Assert.Equal("z2-general", cfg.StrategyWinws2);
        Assert.Equal("bad_value", R.Error(await h.Call("engine", A(("engine", "nfqws")))));
        File.Delete(Path.Combine(h.F.Paths.EngineDir, "winws.exe"));
        Assert.Equal("engine_missing", R.Error(await h.Call("engine", A(("engine", "winws")))));
    }

    [Fact]
    public async Task Settings_GetSet()
    {
        using var h = new Harness();
        var g = await h.Call("settings.get");
        Assert.Equal("winws", R.Str(g["settings"]!["main"]!["engine"]));
        var bad = await h.Call("settings.set", A(("monitor", new JsonObject { ["interval"] = 7 }), ("main", new JsonObject { ["ipv6"] = true })));
        Assert.Equal("bad_value", R.Error(bad));
        Assert.Equal(["monitor.interval"], R.Strings(bad["details"]!["bad_options"]));
        Assert.False(h.D.Context.Config.Load().Ipv6);
        var ok = await h.Call("settings.set", A(("monitor", new JsonObject { ["interval"] = 15 }), ("main", new JsonObject { ["ipv6"] = true })));
        Assert.True(R.IsOk(ok), ok.ToJsonString());
        Assert.Equal(15, h.D.Context.Config.Load().Monitor.Interval);
        Assert.True(h.D.Context.Config.Load().Ipv6);
    }

    [Fact]
    public async Task Wizard_AndPresets()
    {
        using var h = new Harness();
        await h.Call("user.set", A(("id", "user-hosts"), ("text", "mine.example\n")));
        Assert.Equal("unknown_variant", R.Error(await h.Call("wizard.apply", A(("services", new JsonArray("youtube:nope"))))));
        Assert.Equal("bad_args", R.Error(await h.Call("wizard.apply", A(("services", new JsonArray("discord", "discord:voice"))))));
        Assert.Equal("unknown_service", R.Error(await h.Call("wizard.apply", A(("services", new JsonArray("nope"))))));
        Assert.Equal("preset_unavailable", R.Error(await h.Call("wizard.apply", A(("services", new JsonArray("whatsapp"))))));
        Assert.Equal("bad_args", R.Error(await h.Call("wizard.apply", A(("services", new JsonArray())))));
        var r = await h.Call("wizard.apply", A(("services", new JsonArray("youtube:full", "discord:voice", "whatsapp"))));
        Assert.True(R.IsOk(r), r.ToJsonString());
        var cfg = h.D.Context.Config.Load();
        Assert.Contains("zaprett-youtube-full", cfg.Lists);
        Assert.DoesNotContain("zaprett-youtube", cfg.Lists);
        Assert.Contains("zaprett-discord", cfg.Lists);
        Assert.Contains("zaprett-discord-voice", cfg.Ipsets);
        Assert.Contains("user-hosts", cfg.Lists);
        Assert.Equal("works_no", R.Str(r["skipped"]![0]!["reason"]));
        Assert.Equal("full", R.Str(r["variants"]!["youtube"]));
        var p = await h.Call("presets");
        var yt = ((JsonArray)p["services"]!).OfType<JsonObject>().First(s => R.Str(s["id"]) == "youtube");
        Assert.Equal("full", R.Str(yt["enabled_variant"]));
        Assert.False(R.Bool(yt["enabled"]));
        var cf = await h.Call("wizard.apply", A(("services", new JsonArray("cloudflare"))));
        Assert.True(R.IsOk(cf), cf.ToJsonString());
        Assert.NotNull(cf["job"]);
        await h.D.Context.Jobs.WhenIdleAsync();
        Assert.Contains("src-cloudflare_v4", h.D.Context.Config.Load().Ipsets);
        Assert.Contains(h.D.Context.Config.Load().Sources, s => s.Name == "cloudflare_v4" && s.Enabled);
    }

    [Fact]
    public async Task Wizard_NeverSwitchesOffUserLists()
    {
        using var h = new Harness();
        // a preset that names a user list in a set that is not chosen
        var p = JsonNode.Parse(File.ReadAllText(h.F.Paths.PresetsFile))!.AsObject();
        var yt = ((JsonArray)p["services"]!).OfType<JsonObject>().First(s => R.Str(s["id"]) == "youtube");
        ((JsonArray)yt["variants"]![0]!["lists"]!).Add("user-hosts");
        File.WriteAllText(h.F.Paths.PresetsFile, p.ToJsonString());
        var r = await h.Call("wizard.apply", A(("services", new JsonArray("youtube"))));
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.Contains("user-hosts", h.D.Context.Config.Load().Lists);
        Assert.Contains("zaprett-youtube", h.D.Context.Config.Load().Lists);
        Assert.DoesNotContain("zaprett-discord", h.D.Context.Config.Load().Lists);
    }

    [Fact]
    public async Task Dns_Update_Conflicts_Log_Version()
    {
        var upd = new FakeUpdates();
        using var h = new Harness(new CoreOptions { Updates = upd, Version = "0.9.1" });
        Assert.False(R.Bool((await h.Call("dns.status"))["dns"]!["encrypted"]));
        var s = await h.Call("dns.setup");
        Assert.True(R.IsOk(s));
        Assert.Equal("doh", h.D.Context.Config.Load().Dns.Mode);
        Assert.False(R.Bool((await h.Call("dns.setup"))["changed"]));
        Assert.Equal(1, h.F.DnsControl.Setups);
        Assert.True(R.IsOk(await h.Call("dns.setup", A(("enable", false)))));
        Assert.Equal("system", h.D.Context.Config.Load().Dns.Mode);
        Assert.Equal("beta", R.Str((await h.Call("update.check", A(("channel", "beta"))))["channel"]));
        Assert.Equal("bad_value", R.Error(await h.Call("update.check", A(("channel", "x")))));
        Assert.True(R.Bool((await h.Call("update.install"))["installed"]));
        Assert.Equal("block", R.Str((await h.Call("conflicts"))["items"]![0]!["severity"]));
        h.F.Log.Info("line");
        Assert.Contains("I line", R.Strings((await h.Call("log", A(("tail", 5))))["lines"]));
        Assert.Equal("bad_value", R.Error(await h.Call("log", A(("tail", 0)))));
        Assert.Equal("bad_args", R.Error(await h.Call("log", A(("tail", "x")))));
        var v = await h.Call("version");
        Assert.Equal("0.9.1", R.Str(v["version"]));
        Assert.Equal("v72.13", R.Str(v["winws"]));
        using var h2 = new Harness();
        Assert.Equal("not_supported", R.Error(await h2.Call("update.check")));
    }

    [Fact]
    public async Task Diag_MasksSubscriptionQuery_AndPages()
    {
        using var h = new Harness();
        await h.Call("sources.save", A(("name", "priv"), ("type", "list"), ("url", "https://lists.example/l.txt?token=SECRET")));
        var d = await h.Call("diag", A(("full", true)));
        var text = R.Str(d["text"])!;
        Assert.DoesNotContain("SECRET", text);
        Assert.Contains("https://lists.example/l.txt?…", text);
        Assert.Contains("===== Состояние =====", text);
        foreach (var page in new[] { "overview", "lists", "strategies", "diagnostics" })
        {
            var p = await h.Call("page", A(("name", page)));
            Assert.True(R.IsOk(p));
            foreach (var part in CommandDispatcher.Pages[page])
                Assert.True(R.IsOk((JsonObject)p[part]!), $"{page}.{part}: {p[part]!.ToJsonString()}");
        }
        Assert.Equal("bad_value", R.Error(await h.Call("page", A(("name", "nope")))));
    }

    [Fact]
    public async Task Startup_StartsEnabledEngine_AndRollsBackLostTest()
    {
        using var h = new Harness();
        await h.Call("enable");
        Directory.CreateDirectory(h.F.Paths.RunDir);
        File.WriteAllText(Path.Combine(h.F.Paths.RunDir, "test-state.json"),
            "{\"mode\":\"exclusive\",\"was_running\":true,\"strategy\":\"strategy-general\"}");
        await h.F.Engine.StartAsync("test", "x", ["--x"], CancellationToken.None);
        var r = await h.D.StartupAsync(CancellationToken.None);
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.True(R.Bool(r["recovered_test"]));
        Assert.False(File.Exists(Path.Combine(h.F.Paths.RunDir, "test-state.json")));
        Assert.False(h.F.Engine.GetState("test").Running);
        Assert.True(h.F.Engine.GetState("main").Running);
        Assert.False(R.Bool((await h.D.StartupAsync(CancellationToken.None))["started"]));
    }

    sealed class FakeUpdates : IUpdateControl
    {
        public Task<JsonObject> CheckAsync(string channel, CancellationToken ct) =>
            Task.FromResult(new JsonObject { ["ok"] = true, ["channel"] = channel, ["available"] = false });

        public Task<JsonObject> InstallAsync(string channel, CancellationToken ct) =>
            Task.FromResult(new JsonObject { ["ok"] = true, ["installed"] = true });
    }
}
