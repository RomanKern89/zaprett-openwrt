using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Administrator-only actions, temporary engine debugging, default subscriptions.</summary>
public sealed class PolicyTests
{
    static readonly CallerInfo Operator = new("op", false, true);
    static readonly CallerInfo Admin = new("admin", true, false);

    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    sealed class Updates : IUpdateControl
    {
        public Task<JsonObject> CheckAsync(string channel, CancellationToken ct) => Task.FromResult(new JsonObject { ["ok"] = true });
        public Task<JsonObject> InstallAsync(string channel, CancellationToken ct) => Task.FromResult(new JsonObject { ["ok"] = true });
    }

    [Fact]
    public async Task Operators_CannotBringCodeThatRunsAsSystem()
    {
        using var h = new Harness(new CoreOptions { Updates = new Updates() });
        Assert.Equal("access_denied", R.Error(await h.Call("update.install", null, Operator)));
        Assert.True(R.IsOk(await h.Call("update.check", null, Operator)));
        Assert.True(R.IsOk(await h.Call("update.install", null, Admin)));

        var repo = A(("repo", new JsonObject { ["url"] = "https://evil.example/index.json" }));
        Assert.Equal("access_denied", R.Error(await h.Call("settings.set", repo, Operator)));
        Assert.Equal(ConfigDefaults.RepoUrl, h.D.Context.Config.Load().Repo.Url);
        Assert.True(R.IsOk(await h.Call("settings.set", A(("main", new JsonObject { ["ipv6"] = true })), Operator)));
        Assert.True(R.IsOk(await h.Call("settings.set", repo, Admin)));

        // a Lua library from the repository: operators are refused, administrators install it
        await h.Call("settings.set", A(("repo", new JsonObject { ["url"] = ConfigDefaults.RepoUrl })), Admin);
        var lua = "print(1)\n";
        var art = "https://repo.example/files/lib.lua";
        h.F.Http.Downloads[art] = () => Encoding.UTF8.GetBytes(lua);
        h.F.Http.Downloads["https://repo.example/manifests/lib.json"] = () => Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema"] = 1, ["id"] = "lib", ["version"] = "1.0",
            ["artifact"] = new JsonObject { ["url"] = art, ["sha256"] = Files.Sha256Hex(Encoding.UTF8.GetBytes(lua)) },
        }.ToJsonString());
        h.F.Http.Downloads[ConfigDefaults.RepoUrl] = () => Encoding.UTF8.GetBytes(
            "{\"schema\":1,\"items\":[{\"id\":\"lib\",\"type\":\"lua_lib\",\"manifest\":\"https://repo.example/manifests/lib.json\"}]}");
        Assert.True(R.IsOk(await h.Call("repo.install", A(("ids", "lib")), Operator)));
        await h.D.Context.Jobs.WhenIdleAsync();
        Assert.Equal("access_denied", R.Error((JsonObject)h.D.Context.Jobs.Read()!["result"]!));
        Assert.Null(h.D.Context.Store.Scan().Get("lua_lib", "lib"));
        Assert.True(R.IsOk(await h.Call("repo.install", A(("ids", "lib")), Admin)));
        await h.D.Context.Jobs.WhenIdleAsync();
        Assert.Equal("done", R.Str(h.D.Context.Jobs.Read()!["state"]));
        Assert.NotNull(h.D.Context.Store.Scan().Get("lua_lib", "lib"));
    }

    [Fact]
    public async Task Debug_SwitchesItselfOff()
    {
        using var h = new Harness(new CoreOptions { DebugMinutes = 30 });
        await h.Call("start");
        Assert.True(R.IsOk(await h.Call("settings.set", A(("main", new JsonObject { ["debug"] = true })))));
        var cfg = h.D.Context.Config.Load();
        Assert.True(cfg.Debug);
        Assert.Equal(h.D.Context.Now + 1800, cfg.DebugUntil);
        Assert.Equal("--debug=1", h.F.Engine.ArgsOf("main")![0]);
        Assert.NotNull((await h.Call("status"))["debug"]);
        h.F.Clock.Now = h.F.Clock.Now.AddMinutes(10);
        await h.Call("ensure");
        Assert.True(h.D.Context.Config.Load().Debug);
        h.F.Clock.Now = h.F.Clock.Now.AddMinutes(21);
        await h.Call("ensure");
        Assert.False(h.D.Context.Config.Load().Debug);
        Assert.DoesNotContain(h.F.Engine.ArgsOf("main")!, a => a.StartsWith("--debug", StringComparison.Ordinal));
        Assert.Null((await h.Call("status"))["debug"]);
        // debug set by hand without an end time gets one
        h.D.Context.Config.Set("main", new JsonObject { ["debug"] = true });
        await h.Call("ensure");
        Assert.Equal(h.D.Context.Now + 1800, h.D.Context.Config.Load().DebugUntil);
    }

    [Fact]
    public async Task SourcesDefaults_RestoresMissingButNotDeleted()
    {
        using var h = new Harness();
        Assert.True(h.D.Context.Config.DeleteSource("cloudflare_v4"));
        Assert.True(R.IsOk(await h.Call("sources.delete", A(("name", "cloudflare_v6")))));
        var r = await h.Call("sources.defaults");
        Assert.Equal(["cloudflare_v4"], R.Strings(r["added"]));
        var names = h.D.Context.Config.Load().Sources.Select(s => s.Name).ToList();
        Assert.Contains("cloudflare_v4", names);
        Assert.DoesNotContain("cloudflare_v6", names);
        Assert.Empty(R.Strings((await h.Call("sources.defaults"))["added"]));
    }
}
