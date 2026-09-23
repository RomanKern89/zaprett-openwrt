using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Lists;
using Zaprett.Core.Repo;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

public sealed class RepoSourcesTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    /* ---------- subscriptions ---------- */

    [Fact]
    public void Normalize_Lines()
    {
        var n = Sources.NormalizeText("hosts", "\uFEFFExample.com\r\n  a.org  \n# c\n; c\n! c\n*.mask.com\nhas space.com\nпример.рф\nxn--e1afmkfd.xn--p1ai\n\n");
        Assert.Equal("example.com\na.org\nxn--e1afmkfd.xn--p1ai\n", n.Text);
        Assert.Equal(3, n.Valid);
        Assert.Equal(3, n.Invalid);
        var ip = Sources.NormalizeText("ipset", "1.2.3.4/24\n::1\n999.1.1.1\n");
        Assert.Equal("1.2.3.0/24\n::1\n", ip.Text);
        Assert.Null(Sources.NormalizeLine("hosts", new string('a', 5000)));
        var src = ConfigLoader.NormalizeSource("s", new JsonObject { ["url"] = "https://e.com/", ["min_entries"] = 3, ["min_valid_ratio"] = 0.9 });
        Assert.Equal("too_few_entries", Sources.CheckContent(src, new NormalizedList("", 2, 0, 0)));
        Assert.Equal("bad_content", Sources.CheckContent(src, new NormalizedList("", 5, 5, 0)));
        Assert.Null(Sources.CheckContent(src, new NormalizedList("", 9, 1, 0)));
        Assert.True(Sources.IsDue(src, null, 0));
        Assert.False(Sources.IsDue(src, new JsonObject { ["last_update"] = 1000 }, 1000 + 3600));
        Assert.True(Sources.IsDue(src, new JsonObject { ["last_update"] = 1000 }, 1000 + 72 * 3600 - 3600));
    }

    [Fact]
    public async Task Sources_SaveUpdateListDelete()
    {
        using var h = new Harness();
        var url = "https://lists.example/doms.txt";
        var body = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"site{i}.example")) + "\n";
        h.F.Http.Downloads[url] = () => B(body);
        Assert.Equal("bad_value", R.Error(await h.Call("sources.save", A(("name", "mine"), ("type", "list"), ("url", "http://x/")))));
        Assert.Equal("bad_id", R.Error(await h.Call("sources.save", A(("name", "Bad"), ("type", "list"), ("url", url)))));
        var s = await h.Call("sources.save", A(("name", "mine"), ("title", "Мои"), ("type", "list"), ("url", url), ("min_entries", 10), ("enabled", true)));
        Assert.True(R.IsOk(s), s.ToJsonString());
        Assert.True(R.Bool(s["created"]));
        Assert.True(R.IsOk(await h.Call("list.enable", A(("id", "src-mine")))));
        var j = await h.Job("sources.update", A(("names", new JsonArray("mine"))));
        Assert.Equal("done", R.Str(j["state"]));
        Assert.Equal(["mine"], R.Strings(j["result"]!["updated"]));
        var list = (JsonArray)(await h.Call("sources.list"))["sources"]!;
        var mine = list.OfType<JsonObject>().First(x => R.Str(x["name"]) == "mine");
        Assert.Equal(20L, R.Long(mine["entries"]));
        Assert.Equal("ok", R.Str(mine["status"]));
        Assert.True(R.Bool(mine["downloaded"]));
        var item = h.D.Context.Store.Scan().Get("list", "src-mine")!;
        Assert.Equal("url", item.Source);
        Assert.Equal(body.Replace("\r", "", StringComparison.Ordinal), File.ReadAllText(item.File));
        var again = await h.Job("sources.update", A(("names", "mine")));
        Assert.Equal(["mine"], R.Strings(again["result"]!["unchanged"]));

        h.F.Http.Downloads[url] = () => B("only.one\n");
        var few = await h.Job("sources.update", A(("names", "mine")));
        Assert.Equal("failed", R.Str(few["state"]));
        Assert.Equal("too_few_entries", R.Str(few["result"]!["failed"]![0]!["error"]));
        Assert.Equal(body, File.ReadAllText(item.File));
        h.F.Http.Downloads[url] = () => new byte[Sources.MaxSourceBytes + 1];
        Assert.Equal("too_large", R.Str((await h.Job("sources.update", A(("names", "mine"))))["result"]!["failed"]![0]!["error"]));
        h.F.Http.Downloads.TryRemove(url, out _);
        Assert.Equal("download_failed", R.Str((await h.Job("sources.update", A(("names", "mine"))))["result"]!["failed"]![0]!["error"]));
        Assert.Equal("not_found", R.Error((JsonObject)(await h.Job("sources.update", A(("names", "zzz"))))["result"]!));

        var moved = await h.Call("sources.save", A(("name", "mine"), ("type", "list_exclude")));
        Assert.True(R.Bool(moved["type_changed"]));
        var cfg = h.D.Context.Config.Load();
        Assert.Contains("src-mine", cfg.ExcludeLists);
        Assert.DoesNotContain("src-mine", cfg.Lists);
        Assert.False(File.Exists(item.File));

        Assert.True(R.IsOk(await h.Call("sources.delete", A(("name", "refilter_domains")))));
        Assert.Contains("refilter_domains", h.D.Context.Config.Load().DeletedSources);
        Assert.Equal("not_found", R.Error(await h.Call("sources.delete", A(("name", "refilter_domains")))));
        Assert.True(R.IsOk(await h.Call("sources.save", A(("name", "refilter_domains"), ("type", "list"), ("url", url)))));
        Assert.DoesNotContain("refilter_domains", h.D.Context.Config.Load().DeletedSources);
        Assert.True(R.IsOk(await h.Call("sources.delete", A(("name", "mine")))));
        Assert.DoesNotContain("src-mine", h.D.Context.Config.Load().ExcludeLists);
    }

    [Fact]
    public async Task Sources_DueOnlyInAutoupdate_AndBusyGuard()
    {
        using var h = new Harness();
        var url = "https://lists.example/ips.txt";
        h.F.Http.Downloads[url] = () => B("1.1.1.0/24\n2.2.2.0/24\n");
        await h.Call("sources.save", A(("name", "ips"), ("type", "ipset"), ("url", url), ("min_entries", 1), ("enabled", true)));
        await h.Call("settings.set", A(("repo", new JsonObject { ["autoupdate"] = false })));
        var j = await h.Job("autoupdate");
        Assert.Equal("done", R.Str(j["state"]));
        Assert.Equal(["ips"], R.Strings(j["result"]!["sources"]!["updated"]));
        Assert.True(R.Bool(j["result"]!["repo"]!["skipped"]));
        var j2 = await h.Job("autoupdate");
        Assert.Empty(R.Strings(j2["result"]!["sources"]!["updated"]));
        Assert.Equal("Нет подписок для обновления", R.Str(j2["result"]!["sources"]!["message"]));

        var gate = new TaskCompletionSource();
        h.D.Context.Jobs.Start("probe", async ctx =>
        {
            await gate.Task;
            return R.Ok();
        });
        Assert.Equal("job_busy", R.Error(await h.Call("sources.save", A(("name", "x"), ("type", "list"), ("url", url)))));
        Assert.Equal("job_busy", R.Error(await h.Call("sources.delete", A(("name", "ips")))));
        Assert.Equal("job_busy", R.Error(await h.Call("repo.fetch")));
        await h.Call("enable");
        await h.Call("start");
        await h.Call("settings.set", A(("monitor", new JsonObject { ["auto_repair"] = false })));
        Assert.Equal("job_busy", R.Str((await h.Call("monitor.run"))["skipped"]));
        gate.SetResult();
        await h.D.Context.Jobs.WhenIdleAsync();
    }

    /* ---------- repository ---------- */

    const string Base = "https://repo.example/";

    static void Publish(Harness h, string id, string type, string version, string content, params string[] deps)
    {
        var art = Base + "files/" + id + ".txt";
        h.F.Http.Downloads[art] = () => B(content);
        var m = new JsonObject
        {
            ["schema"] = 1, ["id"] = id, ["name"] = id, ["version"] = version, ["author"] = "t",
            ["artifact"] = new JsonObject { ["url"] = art, ["sha256"] = Files.Sha256Hex(B(content)) },
            ["dependencies"] = R.Arr(deps.Select(d => Base + "manifests/" + d + ".json")),
        };
        h.F.Http.Downloads[Base + "manifests/" + id + ".json"] = () => B(m.ToJsonString());
    }

    static void Index(Harness h, params (string Id, string Type)[] items) =>
        h.F.Http.Downloads[ConfigDefaults.RepoUrl] = () => B(new JsonObject
        {
            ["schema"] = 1,
            ["items"] = R.Arr(items.Select(i => (JsonNode?)new JsonObject { ["id"] = i.Id, ["type"] = i.Type, ["manifest"] = Base + "manifests/" + i.Id + ".json" })),
        }.ToJsonString());

    [Fact]
    public async Task Repo_FetchInstallUpgradeRemove()
    {
        using var h = new Harness();
        Publish(h, "bin-x", "bin", "1.0.0", "BINARY");
        Publish(h, "strat-x", "nfqws", "1.0.0", "--filter-tcp=443 ${hostlists} --dpi-desync-fake-tls=${bin:bin-x}", "bin-x");
        Publish(h, "zaprett-youtube", "list", "9999.0.0", "youtube.com\n");
        Index(h, ("bin-x", "bin"), ("strat-x", "nfqws"), ("zaprett-youtube", "list"), ("bye", "byedpi"));
        Publish(h, "bye", "byedpi", "1.0.0", "x");

        var f = await h.Job("repo.fetch");
        Assert.Equal("done", R.Str(f["state"]));
        var list = (JsonArray)(await h.Call("repo.list"))["items"]!;
        Assert.Equal(4, list.Count);
        var yt = list.OfType<JsonObject>().First(i => R.Str(i["id"]) == "zaprett-youtube");
        Assert.True(R.Bool(yt["installed"]));
        Assert.True(R.Bool(yt["update_available"]));
        Assert.Equal("bad_type", R.Error(await h.Call("repo.list", A(("type", "zzz")))));

        var ins = await h.Job("repo.install", A(("ids", new JsonArray("strat-x"))));
        Assert.Equal("done", R.Str(ins["state"]));
        Assert.Equal(["bin-x", "strat-x"], R.Strings(ins["result"]!["installed"]));
        var idx = h.D.Context.Store.Scan();
        Assert.Equal("repo", idx.Get("nfqws", "strat-x")!.Source);
        Assert.Equal(["bin-x"], idx.Get("nfqws", "strat-x")!.Dependencies);
        var sset = await h.Call("strategy.set", A(("id", "strat-x")));
        Assert.True(R.IsOk(sset), sset.ToJsonString());

        var unsupported = await h.Job("repo.install", A(("id", "bye")));
        Assert.Equal("unsupported_item", R.Error((JsonObject)unsupported["result"]!));
        var nf = await h.Job("repo.install", A(("ids", "nope")));
        Assert.Equal("not_in_repo", R.Error((JsonObject)nf["result"]!));

        var up = await h.Job("repo.upgrade", A(("ids", new JsonArray("zaprett-youtube"))));
        Assert.Equal(["zaprett-youtube"], R.Strings(up["result"]!["installed"]));
        Assert.Equal("9999.0.0", h.D.Context.Store.Scan().Get("list", "zaprett-youtube")!.Version);
        var none = await h.Job("repo.upgrade", A(("all", true)));
        Assert.Equal("Обновлений нет", R.Str(none["result"]!["message"]));

        var inUse = await h.Job("repo.remove", A(("id", "bin-x")));
        Assert.Equal("item_in_use", R.Error((JsonObject)inUse["result"]!));
        var active = await h.Job("repo.remove", A(("id", "strat-x")));
        Assert.Equal("item_active", R.Error((JsonObject)active["result"]!));
        var yRem = await h.Job("repo.remove", A(("id", "zaprett-youtube")));
        Assert.Equal("bundle", R.Str(yRem["result"]!["fallback"]));
        Assert.Equal("bundle", h.D.Context.Store.Scan().Get("list", "zaprett-youtube")!.Source);
        var ro = await h.Job("repo.remove", A(("id", "zaprett-discord")));
        Assert.Equal("readonly_item", R.Error((JsonObject)ro["result"]!));
        Assert.Equal("not_installed", R.Error((JsonObject)(await h.Job("repo.remove", A(("id", "nothing"))))["result"]!));
    }

    [Fact]
    public async Task Repo_RejectsBadDownloads()
    {
        using var h = new Harness();
        Publish(h, "bin-y", "bin", "1.0.0", "GOOD");
        Index(h, ("bin-y", "bin"));
        await h.Job("repo.fetch");
        h.F.Http.Downloads[Base + "files/bin-y.txt"] = () => B("EVIL");
        var r = await h.Job("repo.install", A(("ids", "bin-y")));
        Assert.Equal("sha256_mismatch", R.Error((JsonObject)r["result"]!));
        Assert.Null(h.D.Context.Store.Scan().Get("bin", "bin-y"));
        h.F.Http.Downloads[Base + "files/bin-y.txt"] = () => new byte[RepoClient.MaxArtifactBytes + 1];
        Assert.Equal("too_large", R.Error((JsonObject)(await h.Job("repo.install", A(("ids", "bin-y"))))["result"]!));
        h.F.Http.Downloads[ConfigDefaults.RepoUrl] = () => B("{\"schema\":2}");
        Assert.Equal("bad_index", R.Error((JsonObject)(await h.Job("repo.fetch"))["result"]!));
        h.F.Http.Downloads.TryRemove(ConfigDefaults.RepoUrl, out _);
        Assert.Equal("download_failed", R.Error((JsonObject)(await h.Job("repo.fetch"))["result"]!));
        Assert.Equal("bad_args", R.Error(await h.Call("repo.install")));
    }

    [Fact]
    public void Repo_PureParts()
    {
        var (items, skipped) = RepoClient.ParseIndex(R.ParseObject(
            "{\"schema\":1,\"items\":[{\"id\":\"a\",\"type\":\"list\",\"manifest\":\"https://h/a.json\"},{\"id\":\"a\",\"type\":\"list\",\"manifest\":\"https://h/a.json\"}," +
            "{\"id\":\"bad id\",\"type\":\"list\",\"manifest\":\"https://h/x.json\"},{\"id\":\"b\",\"type\":\"zzz\",\"manifest\":\"https://h/b.json\"}," +
            "{\"id\":\"c\",\"type\":\"nfqws\",\"manifest\":\"https://h/c.json\"}]}"));
        Assert.Equal(["a", "c"], items!.Select(i => i.Id));
        Assert.Equal(3, skipped);
        Assert.Null(RepoClient.ParseIndex(R.ParseObject("{\"schema\":1}")).Items);
        var idx = new IndexItem("a", "list", "https://h/a.json");
        Assert.Equal("bad_manifest", RepoClient.ParseManifest(R.ParseObject("{\"schema\":2}"), idx).Error);
        Assert.Equal("id_mismatch", RepoClient.ParseManifest(R.ParseObject("{\"schema\":1}"), idx).Error);
        Assert.Equal("bad_version", RepoClient.ParseManifest(R.ParseObject("{\"schema\":1,\"id\":\"a\",\"version\":\"\"}"), idx).Error);
        Assert.Equal("bad_artifact", RepoClient.ParseManifest(R.ParseObject("{\"schema\":1,\"id\":\"a\",\"version\":\"1\",\"artifact\":{}}"), idx).Error);
        var sha = new string('A', 64);
        var m = RepoClient.ParseManifest(R.ParseObject("{\"schema\":1,\"id\":\"a.\",\"version\":\"1\",\"artifact\":{\"url\":\"https://h/f\",\"sha256\":\"" + sha + "\"}}"), idx);
        Assert.NotNull(m.Manifest);
        Assert.NotNull(m.Manifest!.IdNote);
        Assert.Equal(sha.ToLowerInvariant(), m.Manifest.ArtifactSha256);
        Assert.Equal("bad_dependencies", RepoClient.ParseManifest(R.ParseObject(
            "{\"schema\":1,\"id\":\"a\",\"version\":\"1\",\"artifact\":{\"url\":\"https://h/f\",\"sha256\":\"" + sha + "\"},\"dependencies\":[\"not url\"]}"), idx).Error);
        var inst = new Store.StoreItem("a", "list", "a", "1", "", "", null, null, [], "", "repo", new string('b', 64), null, null, null, null);
        Assert.True(RepoClient.IsUpdate(inst, m.Manifest));
        Assert.False(RepoClient.IsUpdate(inst with { Source = "bundle" }, m.Manifest));
        Assert.False(RepoClient.IsUpdate(null, m.Manifest));
        JsonObject Node(string id, params string[] deps) => new()
        {
            ["id"] = id, ["type"] = "bin", ["manifest"] = new JsonObject { ["dependencies"] = R.Arr(deps) },
        };
        var a = Node("a", "u:b");
        var b = Node("b", "u:a");
        var byUrl = new Dictionary<string, JsonObject> { ["u:a"] = a, ["u:b"] = b };
        Assert.Equal(["b", "a"], RepoClient.ResolveOrder([a], byUrl).Order!.Select(x => R.Str(x["id"])));
        Assert.Equal("u:zz", RepoClient.ResolveOrder([Node("c", "u:zz")], byUrl).Url);
    }
}
