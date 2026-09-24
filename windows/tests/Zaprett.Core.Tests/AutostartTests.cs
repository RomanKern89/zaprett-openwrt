using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>main.autostart (on at Windows start) is separate from main.enabled (on now): switching autostart never touches
/// the running engine; at service startup enabled becomes autostart and nothing else changes.</summary>
public sealed class AutostartTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    static void WriteMain(Harness h, JsonObject main)
    {
        var doc = ConfigDefaults.Document();
        var m = (JsonObject)doc["main"]!;
        m.Remove("autostart");
        foreach (var (k, v) in main)
            m[k] = v?.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(h.F.Paths.ConfigFile)!);
        File.WriteAllText(h.F.Paths.ConfigFile, doc.ToJsonString());
    }

    static JsonObject RawMain(Harness h) => (JsonObject)JsonNode.Parse(File.ReadAllText(h.F.Paths.ConfigFile))!["main"]!;

    /// <summary>A reboot: the engine is gone and a new core starts over the same data (the first service start since
    /// Windows booted; the old overload means the same).</summary>
    static async Task<(CommandDispatcher D, JsonObject R)> RebootAsync(Harness h)
    {
        h.F.Engine.Kill("main");
        var d = new CommandDispatcher(h.F.Services);
        return (d, await d.StartupAsync(CancellationToken.None));
    }

    /// <summary>A restart of the service only (update, crash, sc restart): the engine went down with it.</summary>
    static async Task<(CommandDispatcher D, JsonObject R)> ServiceRestartAsync(Harness h)
    {
        h.F.Engine.Kill("main");
        var d = new CommandDispatcher(h.F.Services);
        return (d, await d.StartupAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task ServiceRestart_KeepsTheBypassOn_EvenWithAutostartOff()
    {
        using var h = new Harness();
        await h.Call("start");
        await h.Call("autostart", A(("enable", false)));
        var (_, r) = await ServiceRestartAsync(h);
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.True(h.D.Context.Config.Load().Enabled);
        Assert.False(h.D.Context.Config.Load().Autostart);
        Assert.True(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task ServiceRestart_KeepsTheBypassOff_EvenWithAutostartOn()
    {
        using var h = new Harness();
        await h.Call("disable");
        await h.Call("autostart", A(("enable", true)));
        await ServiceRestartAsync(h);
        Assert.False(h.D.Context.Config.Load().Enabled);
        Assert.False(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task ServiceRestart_KeepsTheUsersStop_ABootForgetsIt()
    {
        using var h = new Harness();
        await h.Call("enable");
        await h.Call("start");
        await h.Call("stop");
        Assert.True(h.D.Context.UserStopped);
        await ServiceRestartAsync(h);
        Assert.True(h.D.Context.UserStopped);
        Assert.False(h.F.Engine.GetState("main").Running);
        var (_, r) = await RebootAsync(h);
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.False(h.D.Context.UserStopped);
        Assert.True(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task Boot_WithAutostartOff_ForgetsTheStop_AndStaysOff()
    {
        using var h = new Harness();
        await h.Call("start");
        await h.Call("stop");
        await h.Call("autostart", A(("enable", false)));
        await RebootAsync(h);
        Assert.False(h.D.Context.UserStopped);
        Assert.False(h.D.Context.Config.Load().Enabled);
        Assert.False(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task AnExplicitBoot_AppliesAutostart()
    {
        using var h = new Harness();
        await h.Call("start");
        await h.Call("autostart", A(("enable", false)));
        h.F.Engine.Kill("main");
        await new CommandDispatcher(h.F.Services).StartupAsync(true, CancellationToken.None);
        Assert.False(h.D.Context.Config.Load().Enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOldConfigWithoutTheField_KeepsAutostartEqualToEnabled(bool enabled)
    {
        using var h = new Harness();
        WriteMain(h, A(("enabled", enabled)));
        var s = await h.Call("status");
        Assert.Equal(enabled, R.Bool(s["autostart"]));
        Assert.DoesNotContain("autostart", R.Strings(s["details"]!["bad_options"]));
        var (_, r) = await RebootAsync(h);
        Assert.Equal(enabled, h.F.Engine.GetState("main").Running);
        Assert.Equal(enabled, h.D.Context.Config.Load().Enabled);
        Assert.True(R.IsOk(r), r.ToJsonString());
    }

    [Fact]
    public async Task ABadValue_IsReported_AndFallsBackToEnabled()
    {
        using var h = new Harness();
        WriteMain(h, A(("enabled", true), ("autostart", "yes please")));
        var cfg = h.D.Context.Config.Load();
        Assert.True(cfg.Autostart);
        Assert.Contains("autostart", cfg.BadOptions);
    }

    [Fact]
    public async Task SwitchingAutostart_NeverStopsOrStartsTheEngine()
    {
        using var h = new Harness();
        Assert.True(R.IsOk(await h.Call("start")));
        var before = h.F.Engine.Log.Count;
        var off = await h.Call("autostart", A(("enable", false)));
        Assert.True(R.IsOk(off), off.ToJsonString());
        Assert.False(R.Bool(off["autostart"]));
        Assert.True(R.Bool(off["enabled"]));
        Assert.True(h.F.Engine.GetState("main").Running);
        Assert.Equal(before, h.F.Engine.Log.Count);
        var s = await h.Call("status");
        Assert.True(R.Bool(s["autostart_separate"]));
        Assert.False(R.Bool(s["autostart"]));
        Assert.True(R.Bool(s["enabled"]));
        Assert.True(R.Bool(s["running"]));
        // the watchdog follows enabled, not autostart: a crash is still repaired
        h.F.Engine.Kill("main");
        Assert.Equal("started", R.Str((await h.Call("ensure"))["action"]));

        await h.Call("stop");
        before = h.F.Engine.Log.Count;
        Assert.True(R.IsOk(await h.Call("autostart", A(("enable", true)))));
        Assert.False(h.F.Engine.GetState("main").Running);
        Assert.Equal(before, h.F.Engine.Log.Count);
        Assert.True(RawMain(h)["autostart"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Reboot_WithAutostartOff_LeavesTheBypassOff_AndTheSettingsAsTheyWere()
    {
        using var h = new Harness();
        await h.Call("start");
        await h.Call("autostart", A(("enable", false)));
        await h.Call("strategy.set", A(("id", "strategy-alt")));
        var mainBefore = RawMain(h);
        var (d, r) = await RebootAsync(h);
        Assert.False(R.Bool(r["started"]));
        Assert.False(h.F.Engine.GetState("main").Running);
        var cfg = h.D.Context.Config.Load();
        Assert.False(cfg.Enabled);
        Assert.False(cfg.Autostart);
        Assert.Equal("strategy-alt", cfg.Strategy);
        // only main.enabled changed
        var mainAfter = RawMain(h);
        mainBefore["enabled"] = false;
        Assert.True(JsonNode.DeepEquals(mainBefore, mainAfter), mainAfter.ToJsonString());
        var s = await d.InvokeAsync("status", null, CallerInfo.System, CancellationToken.None);
        Assert.False(R.Bool(s["enabled"]));
        // the watchdog does not start it either
        Assert.NotEqual("started", R.Str((await d.InvokeAsync("ensure", null, CallerInfo.System, CancellationToken.None))["action"]));
        Assert.False(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task Reboot_WithAutostartOn_SwitchesTheBypassOn()
    {
        using var h = new Harness();
        await h.Call("disable");
        await h.Call("autostart", A(("enable", true)));
        Assert.False(h.D.Context.Config.Load().Enabled);
        Assert.False(h.F.Engine.GetState("main").Running);
        var (_, r) = await RebootAsync(h);
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.True(h.D.Context.Config.Load().Enabled);
        Assert.True(h.F.Engine.GetState("main").Running);
    }

    [Fact]
    public async Task EnableAndDisable_KeepTheirOldMeaning()
    {
        using var h = new Harness();
        var e = await h.Call("enable");
        Assert.True(R.Bool(e["enabled"]) && R.Bool(e["autostart"]));
        var cfg = h.D.Context.Config.Load();
        Assert.True(cfg.Enabled && cfg.Autostart);
        await h.Call("start");
        var d = await h.Call("disable");
        Assert.True(R.IsOk(d));
        cfg = h.D.Context.Config.Load();
        Assert.False(cfg.Enabled || cfg.Autostart);
        Assert.False(h.F.Engine.GetState("main").Running);
        // start and stop do not change autostart
        await h.Call("start");
        Assert.False(h.D.Context.Config.Load().Autostart);
        await h.Call("autostart", A(("enable", true)));
        await h.Call("stop");
        Assert.True(h.D.Context.Config.Load().Autostart);
    }

    [Fact]
    public async Task WizardApply_SetsAutostartOnlyWhenAsked()
    {
        using var h = new Harness();
        var r = await h.Call("wizard.apply", A(("services", new JsonArray("youtube")), ("autostart", true)));
        Assert.True(R.IsOk(r), r.ToJsonString());
        var cfg = h.D.Context.Config.Load();
        Assert.True(cfg.Autostart);
        Assert.False(cfg.Enabled);
        r = await h.Call("wizard.apply", A(("services", new JsonArray("youtube"))));
        Assert.True(R.IsOk(r));
        Assert.True(h.D.Context.Config.Load().Autostart);
        Assert.Equal("bad_args", R.Error(await h.Call("wizard.apply", A(("services", new JsonArray("youtube")), ("autostart", "maybe")))));
    }

    [Fact]
    public async Task TheMethod_ChecksItsArgument_AndTheCaller()
    {
        using var h = new Harness();
        Assert.Equal("bad_args", R.Error(await h.Call("autostart")));
        Assert.Equal("bad_args", R.Error(await h.Call("autostart", A(("enable", 2)))));
        Assert.True(R.IsOk(await h.Call("autostart", A(("enable", "1")))));
        Assert.True(h.D.Context.Config.Load().Autostart);
        Assert.Equal("access_denied", R.Error(await h.Call("autostart", A(("enable", false)), Harness.User)));
        Assert.True(h.D.Context.Config.Load().Autostart);
    }
}
