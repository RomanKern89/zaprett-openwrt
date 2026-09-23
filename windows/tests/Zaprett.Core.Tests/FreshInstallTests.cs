using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>A clean installation (Windows 10 test machine): no user list files and no guard files yet, the engine never started. The
/// fake engine refuses a missing list file exactly like winws ("cannot access hostlist file"), so a check that forgets
/// to create them fails both the new and the old configuration and gives a false config_was_invalid.</summary>
public sealed class FreshInstallTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    static Harness FreshInstall()
    {
        var h = new Harness();
        // what the installer writes: the defaults, service off
        File.WriteAllText(h.F.Paths.ConfigFile, ConfigDefaults.Document().ToJsonString());
        Assert.False(Directory.Exists(h.F.Paths.UserDir));
        Assert.False(Directory.Exists(Path.Combine(h.F.Paths.DataDir, "guard")));
        return h;
    }

    [Fact]
    public async Task WizardApply_OnFreshInstall_HasNoConfigWasInvalid()
    {
        using var h = FreshInstall();
        var r = await h.Call("wizard.apply", A(("services", new JsonArray("youtube", "discord"))));
        Assert.True(R.IsOk(r), r.ToJsonString());
        Assert.DoesNotContain("config_was_invalid", R.Strings(r["warnings"]));
        Assert.True(File.Exists(Path.Combine(h.F.Paths.UserDir, "hosts-include.txt")));
        Assert.True(File.Exists(Path.Combine(h.F.Paths.DataDir, "guard", "hostlist-guard.txt")));
        Assert.DoesNotContain(h.F.Log.Lines, l => l.Contains("dry_run", StringComparison.Ordinal) || l.Contains("не прошла", StringComparison.Ordinal));
        Assert.True(R.IsOk(await h.Call("start")));
    }

    [Fact]
    public async Task OtherChecksOnFreshInstall_Pass()
    {
        using var h = FreshInstall();
        Assert.True(R.IsOk(await h.Call("strategy.set", A(("id", "strategy-alt")))));
        using var h2 = FreshInstall();
        var m = await h2.Call("mode", A(("mode", "blacklist")));
        Assert.True(R.IsOk(m), m.ToJsonString());
        Assert.DoesNotContain("config_was_invalid", R.Strings(m["warnings"]));
        using var h3 = FreshInstall();
        Assert.True(R.IsOk(await h3.Call("check")));
    }

    [Fact]
    public async Task ReallyBrokenSettings_StillFail_AndTheReasonIsLogged()
    {
        using var h = FreshInstall();
        // the engine rejects every argument set with ipv6: the new configuration fails, the old one passes
        h.F.Processes.Handler = (f, a) => a.Contains("--wf-l3=ipv4,ipv6")
            ? new Platform.ProcessResult(1, "", "bad value for --wf-l3", false) : new Platform.ProcessResult(0, "", "", false);
        var r = await h.Call("settings.set", A(("main", new JsonObject { ["ipv6"] = true })));
        Assert.Equal("dry_run_failed", R.Error(r));
        Assert.False(h.D.Context.Config.Load().Ipv6);
        Assert.Contains(h.F.Log.Lines, l => l.StartsWith("W ", StringComparison.Ordinal) && l.Contains("bad value for --wf-l3", StringComparison.Ordinal)
            && l.Contains("strategy-general", StringComparison.Ordinal));
        // both configurations broken: allowed with config_was_invalid, and both failures are in the log
        h.F.Processes.Handler = (_, _) => new Platform.ProcessResult(1, "", "broken engine", false);
        var both = await h.Call("mode", A(("mode", "blacklist")));
        Assert.True(R.IsOk(both));
        Assert.Contains("config_was_invalid", R.Strings(both["warnings"]));
        Assert.True(h.F.Log.Lines.Count(l => l.Contains("broken engine", StringComparison.Ordinal)) >= 2);
    }
}
