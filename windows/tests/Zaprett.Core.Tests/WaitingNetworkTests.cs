using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>With a network filter winws runs but waits for the selected network ("logical network is not present. waiting
/// it to appear."): the service reports the instance as running in phase waiting_network.</summary>
public sealed class WaitingNetworkTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    [Fact]
    public async Task Status_ShowsThePhase_AndTheWarning()
    {
        using var h = new Harness();
        Assert.True(R.IsOk(await h.Call("start")));
        var s = await h.Call("status");
        Assert.Equal("capturing", R.Str(s["engine_stats"]!["phase"]));
        Assert.DoesNotContain("waiting_network", R.Strings(s["warnings"]));
        h.F.Engine.Phase = EnginePhases.WaitingNetwork;
        s = await h.Call("status");
        Assert.True(R.Bool(s["running"]));
        Assert.Equal("waiting_network", R.Str(s["engine_stats"]!["phase"]));
        Assert.Contains("waiting_network", R.Strings(s["warnings"]));
        Assert.DoesNotContain("not_running", R.Strings(s["warnings"]));
    }

    [Fact]
    public async Task Monitor_DoesNotProbeOrRepair_WhileWaitingForTheNetwork()
    {
        using var h = new Harness();
        await h.Call("settings.set", A(("monitor", new JsonObject { ["threshold"] = 1, ["auto_repair"] = true })));
        await h.Call("start");
        h.F.Http.Probe = r => new ProbeResult(r.Url, false, 1, 0, "timeout", null);
        h.F.Engine.Phase = EnginePhases.WaitingNetwork;
        for (var i = 0; i < 3; i++)
            Assert.Equal("waiting_network", R.Str((await h.Call("monitor.run"))["skipped"]));
        Assert.Empty(h.F.Http.Requests);
        Assert.Null(h.D.Context.Jobs.Read());
        // control: the same failures while capturing do start the repair
        h.F.Engine.Phase = EnginePhases.Capturing;
        var r = await h.Call("monitor.run");
        Assert.Null(r["skipped"]);
        Assert.NotEmpty(h.F.Http.Requests);
        Assert.True(R.Bool(r["repair_started"]), r.ToJsonString());
    }

    [Fact]
    public void EngineState_DefaultsToCapturing()
    {
        Assert.Equal("capturing", new EngineState("main", true, 1, null, 0).Phase);
        Assert.Equal("waiting_network", new EngineState("main", true, 1, null, 0, EnginePhases.WaitingNetwork).Phase);
    }
}
