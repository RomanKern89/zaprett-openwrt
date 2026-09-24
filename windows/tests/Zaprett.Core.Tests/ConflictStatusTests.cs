using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Another winws/WinDivert/GoodbyeDPI (severity block) breaks the traffic whatever the strategy: status must say so,
/// and the monitor must not replace the strategy because of it.</summary>
public sealed class ConflictStatusTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    [Fact]
    public async Task Status_ReportsABlockingConflict_WithNamePathAndPid()
    {
        using var h = new Harness();
        await h.Call("start");
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        h.F.Conflicts.Items.Add(FakeConflicts.Vpn());
        var s = await h.Call("status");
        Assert.Contains("conflict_blocking", R.Strings(s["warnings"]));
        var list = (JsonArray)s["conflicts_blocking"]!;
        var only = Assert.Single(list);
        Assert.Equal("zapret", R.Str(only!["id"]));
        Assert.Equal("zapret / winws (another installation)", R.Str(only["name"]));
        Assert.Equal(@"C:\zt\foreign\winws.exe", R.Str(only["path"]));
        Assert.Equal(4242L, R.Long(only["pid"]));
        Assert.Equal("process", R.Str(only["kind"]));
        Assert.Null(only["service"]);
        Assert.True(R.Bool(s["running"]));
    }

    [Fact]
    public async Task Status_NoWarning_ForNonBlockingItems_OrAFailedScan()
    {
        using var h = new Harness();
        h.F.Conflicts.Items.Add(FakeConflicts.Vpn());
        var s = await h.Call("status");
        Assert.DoesNotContain("conflict_blocking", R.Strings(s["warnings"]));
        Assert.Empty((JsonArray)s["conflicts_blocking"]!);
        // a scan without path/pid (old scanner) still reports, with nulls
        var noPath = FakeConflicts.Winws();
        noPath.Remove("path");
        noPath.Remove("pid");
        h.F.Conflicts.Items.Add(noPath);
        var only = Assert.Single((JsonArray)(await h.Call("status"))["conflicts_blocking"]!);
        Assert.Null(only!["path"]);
        Assert.Null(only["pid"]);
        // a broken scanner does not break status
        h.F.Conflicts.Fail = true;
        s = await h.Call("status");
        Assert.True(R.IsOk(s), s.ToJsonString());
        Assert.DoesNotContain("conflict_blocking", R.Strings(s["warnings"]));
        Assert.Contains(h.F.Log.Lines, l => l.StartsWith("W ", StringComparison.Ordinal) && l.Contains("scan failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Monitor_DoesNotRepair_WhenAnotherProgramBlocks_AndRepairsAfterItIsGone()
    {
        using var h = new Harness();
        await h.Call("settings.set", A(("monitor", new JsonObject { ["threshold"] = 1, ["auto_repair"] = true })));
        await h.Call("start");
        h.F.Http.Probe = r => new ProbeResult(r.Url, false, 1, 0, "timeout", null);
        h.F.Conflicts.Items.Add(FakeConflicts.Winws());
        var r = await h.Call("monitor.run");
        // D22: a failure with a known cause (the other program) does not count towards degraded
        Assert.Equal("ok", R.Str(r["state"]));
        Assert.Equal(0L, R.Long(r["consecutive_failures"]));
        Assert.False(R.Bool(r["repair_started"]));
        Assert.Equal("conflict_blocking", R.Str(r["repair_blocked"]));
        Assert.Single((JsonArray)r["conflicts_blocking"]!);
        Assert.Null(h.D.Context.Jobs.Read());
        Assert.Contains(h.F.Log.Lines, l => l.StartsWith("W ", StringComparison.Ordinal) && l.Contains("zapret / winws", StringComparison.Ordinal));
        // kept in the monitor state: monitor.status, the "monitor" event and status.monitor
        Assert.Equal("conflict_blocking", R.Str((await h.Call("monitor.status"))["monitor"]!["repair_blocked"]));
        Assert.Equal("conflict_blocking", R.Str((await h.Call("status"))["monitor"]!["repair_blocked"]));
        Assert.Contains(h.Events, e => e.Type == "monitor" && R.Str(e.Data["repair_blocked"]) == "conflict_blocking");
        // the other program is gone: the same failures start the repair (last_repair was not spent on the blocked one)
        h.F.Conflicts.Items.Clear();
        r = await h.Call("monitor.run");
        Assert.True(R.Bool(r["repair_started"]), r.ToJsonString());
        Assert.Null(r["repair_blocked"]);
        Assert.Null((await h.Call("monitor.status"))["monitor"]!["repair_blocked"]);
        Assert.Null((await h.Call("status"))["monitor"]!["repair_blocked"]);
    }

    [Fact]
    public async Task Monitor_ScansBeforeAndAfterTheProbes()
    {
        using var h = new Harness();
        await h.Call("settings.set", A(("monitor", new JsonObject { ["threshold"] = 1, ["auto_repair"] = true })));
        await h.Call("start");
        var before = h.F.Conflicts.Scans;
        var r = await h.Call("monitor.run");
        Assert.Equal("ok", R.Str(r["state"]));
        Assert.Equal(before + 2, h.F.Conflicts.Scans);
        Assert.False(R.Bool(((JsonObject)(await h.Call("monitor.status"))["monitor"]!)["conflict_blocking"]));
    }
}
