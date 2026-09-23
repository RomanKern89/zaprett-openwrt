using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Store;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

public sealed class StoreTests
{
    static void WriteInstalled(TestPaths p, string type, string id, JsonObject manifest, string content)
    {
        var dir = ItemTypes.DirOf(type);
        var file = Path.Combine(p.InstalledDir, "files", dir, id + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        var m = Path.Combine(p.InstalledDir, "manifests", dir, id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(m)!);
        manifest["file"] ??= file;
        File.WriteAllText(m, manifest.ToJsonString());
    }

    static JsonObject Manifest(string id, string type, string version = "2.0.0") => new()
    {
        ["schema"] = 1, ["id"] = id, ["type"] = type, ["name"] = id, ["version"] = version, ["sha256"] = new string('a', 64),
    };

    [Fact]
    public void Scan_FindsBundle()
    {
        using var p = new TestPaths();
        var store = new ItemStore(p);
        var idx = store.Scan();
        Assert.Empty(idx.Errors);
        Assert.Equal(64, idx.Items["nfqws"].Count);
        Assert.Equal(14, idx.Items["nfqws2"].Count);
        var g = idx.Get("nfqws", "strategy-general")!;
        Assert.Equal("bundle", g.Source);
        Assert.True(Files.IsUnder(g.File, Path.Combine(p.BundleDir, "files")));
        Assert.True(File.Exists(g.File));
        Assert.Contains("quic_initial_www_google_com", g.Dependencies);
        Assert.Equal("user", idx.Get("list", "user-hosts")!.Source);
        Assert.Contains("strategy-general", idx.UsedBy("quic_initial_www_google_com"));
    }

    [Fact]
    public void Installed_OverridesBundle_AndUserStrategiesAppear()
    {
        using var p = new TestPaths();
        WriteInstalled(p, "list", "zaprett-youtube", Manifest("zaprett-youtube", "list"), "youtube.com\n");
        var ud = Path.Combine(p.UserDir, "strategies", "winws");
        Directory.CreateDirectory(ud);
        File.WriteAllText(Path.Combine(ud, "user-mine.txt"), "--filter-tcp=443 ${hostlists}");
        File.WriteAllText(Path.Combine(ud, "not-user.txt"), "x");
        var idx = new ItemStore(p).Scan();
        var yt = idx.Get("list", "zaprett-youtube")!;
        Assert.Equal("repo", yt.Source);
        Assert.Equal("2.0.0", yt.Version);
        Assert.NotNull(idx.Get("nfqws", "user-mine"));
        Assert.Null(idx.Get("nfqws", "not-user"));
    }

    [Fact]
    public void BadManifests_AreRejectedWithReasons()
    {
        using var p = new TestPaths();
        WriteInstalled(p, "list", "outside", Manifest("outside", "list").Also(m => m["file"] = "C:\\Windows\\win.ini"), "");
        WriteInstalled(p, "list", "mismatch", Manifest("other", "list"), "");
        WriteInstalled(p, "list", "typebad", Manifest("typebad", "ipset"), "");
        WriteInstalled(p, "list", "nosha", Manifest("nosha", "list").Also(m => m.Remove("sha256")), "");
        WriteInstalled(p, "list", "router-etc", Manifest("router-etc", "list").Also(m => m["file"] = "/etc/passwd"), "");
        var missing = Manifest("gone", "list");
        missing["file"] = Path.Combine(p.InstalledDir, "files", "lists", "include", "absent.txt");
        WriteInstalled(p, "list", "gone", missing, "");
        var idx = new ItemStore(p).Scan();
        var errors = idx.Errors.ToDictionary(e => Path.GetFileNameWithoutExtension(e.Path), e => e.Error);
        Assert.Equal("file_outside_root", errors["outside"]);
        Assert.Equal("id_mismatch", errors["mismatch"]);
        Assert.Equal("type_mismatch", errors["typebad"]);
        Assert.Equal("no_sha256", errors["nosha"]);
        Assert.Equal("file_outside_root", errors["router-etc"]);
        Assert.Equal("file_missing", errors["gone"]);
    }

    [Theory]
    [InlineData("/usr/share/zaprett/bundle/files/bin/x.bin", "bin", "bin\\x.bin")]
    [InlineData("/etc/zaprett/files/lists/include/a.txt", "list", "lists\\include\\a.txt")]
    [InlineData("a.txt", "list", "lists\\include\\a.txt")]
    [InlineData("/usr/share/zaprett/bundle/files/../../x", "bin", null)]
    [InlineData("/tmp/x", "bin", null)]
    [InlineData("C:x.txt", "bin", null)]
    [InlineData("..\\x.txt", "bin", null)]
    public void ResolveManifestFile(string file, string type, string? rel)
    {
        var root = "C:\\root";
        var r = ItemStore.ResolveManifestFile(file, type, root);
        Assert.Equal(rel == null ? null : "C:\\root\\files\\" + rel, r);
    }

    [Fact]
    public void Entries_CountedAndCached()
    {
        using var p = new TestPaths();
        var store = new ItemStore(p);
        p.WriteUser("hosts-include.txt", "a.com\n#c\nb.com\n");
        var idx = store.Scan();
        var it = idx.Get("list", "user-hosts")!;
        Assert.Equal(2, store.EntriesOf(it));
        File.WriteAllText(it.File, "a.com\nb.com\nc.com\n");
        File.SetLastWriteTimeUtc(it.File, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(3, store.EntriesOf(it));
        Assert.Equal(0, store.EntriesOf(idx.Get("list_exclude", "user-hosts-exclude")!));
        Assert.Null(store.EntriesOf(idx.Get("nfqws", "strategy-general")!));
        var gz = Path.Combine(p.UserDir, "ipset-include.txt");
        File.WriteAllBytes(gz, [0x1f, 0x8b, 1, 2]);
        Assert.Null(store.EntriesOf(idx.Get("ipset", "user-ipset")!));
    }

    [Fact]
    public void Describe_MarksActiveItems()
    {
        using var p = new TestPaths();
        var store = new ItemStore(p);
        var idx = store.Scan();
        var cfg = ConfigLoader.Normalize(null);
        var items = store.Describe(idx, cfg, null).OfType<JsonObject>().ToList();
        bool Active(string id) => R.Bool(items.First(i => R.Str(i["id"]) == id)["active"]);
        Assert.True(Active("zaprett-youtube"));
        Assert.False(Active("zaprett-telegram"));
        Assert.True(Active("strategy-general"));
        Assert.True(Active("quic_initial_www_google_com"));
        Assert.False(Active("z2-general"));
        Assert.All(store.Describe(idx, cfg, "bin").OfType<JsonObject>(), i => Assert.Equal("bin", R.Str(i["type"])));
        Assert.NotNull(store.BundleItem("list", "zaprett-youtube"));
        Assert.Null(store.BundleItem("list", "nope"));
        Assert.Equal("zaprett-x", ItemStore.DepToId("https://h/manifests/zaprett-x.json"));
        Assert.Null(ItemStore.DepToId("https://h/bad id.json"));
    }
}

static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> a)
    {
        a(o);
        return o;
    }
}
