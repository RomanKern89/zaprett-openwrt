using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Store;
using Zaprett.Core.Strategy;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Router conformance (ARCHITECTURE-WIN §4): the reference argv of the router generator (tests/conformance,
/// made by tools/conformance on a lab router) must equal the C# argv after removing the router base options and
/// mapping the router paths onto the Windows layout.</summary>
public sealed class ConformanceTests
{
    /// <summary>Cases where Windows deliberately differs from the router, with the error the C# generator must give.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownDivergences = new Dictionary<string, string>
    {
        // --lua-init only from the Lua directories on Windows (task wincore §3); the router accepts any zaprett file
        ["custom/nfqws2-own-lua-init"] = "path_not_allowed",
    };

    static readonly string[] RouterBasePrefixes =
    [
        "--debug=syslog", "--qnum=", "--user=", "--dpi-desync-fwmark=", "--fwmark=", "--lua-init=@/usr/share/zaprett/lua/",
    ];

    public static IEnumerable<object[]> Cases() =>
        Directory.GetFiles(RepoPaths.ConformanceDir, "*.json").Order(StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    [Fact]
    public void ReferenceSet_HasEnoughCases()
    {
        var files = Directory.GetFiles(RepoPaths.ConformanceDir, "*.json");
        Assert.True(files.Length >= 40, $"only {files.Length} conformance cases");
        var names = files.Select(f => R.Str(Files.ReadJson(f)!["name"])!).ToList();
        Assert.Equal(64, names.Count(n => n.StartsWith("base/nfqws/", StringComparison.Ordinal)));
        Assert.Equal(14, names.Count(n => n.StartsWith("base/nfqws2/", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CSharpGenerator_MatchesRouter(string file)
    {
        var c = Files.ReadJson(Path.Combine(RepoPaths.ConformanceDir, file))!;
        using var paths = new TestPaths();
        var (res, cfg) = Run(c, paths);
        var name = R.Str(c["name"])!;
        if (KnownDivergences.TryGetValue(name, out var err))
        {
            Assert.False(res.Ok, $"{name}: expected divergence {err}");
            Assert.Equal(err, res.Error);
            return;
        }
        var diffs = Compare(c, res, cfg, paths);
        Assert.True(diffs.Count == 0, $"{name}:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void CorruptedReference_FailsComparison()
    {
        // negative control: every kind of damage to a reference must be detected
        var file = Path.Combine(RepoPaths.ConformanceDir, "074-base--nfqws2--z2-general.json");
        var orig = Files.ReadJson(file)!;
        using var paths = new TestPaths();
        var (res, cfg) = Run(orig, paths);
        Assert.Empty(Compare(orig, res, cfg, paths));

        var corruptions = new List<Action<JsonObject>>
        {
            e => ((JsonArray)e["args"]!).RemoveAt(((JsonArray)e["args"]!).Count - 1),
            e => ((JsonArray)e["args"]!)[5] = "--hostlist=/usr/share/zaprett/bundle/files/lists/include/zaprett-other.txt",
            e => { var a = (JsonArray)e["args"]!; var x = a[6]!.DeepClone(); a[6] = a[7]!.DeepClone(); a[7] = x; },
            e => ((JsonArray)e["args"]!).Add("--new"),
            e => ((JsonArray)((JsonObject)e["ports"]!)["tcp"]!).Add("8080"),
            e => ((JsonArray)e["warnings"]!).Add("no_active_lists"),
            e => e["ok"] = false,
            e => e["details"] = new JsonObject { ["empty_profiles"] = 1 },
        };
        foreach (var corrupt in corruptions)
        {
            var bad = orig.DeepClone().AsObject();
            corrupt((JsonObject)bad["expected"]!);
            Assert.NotEmpty(Compare(bad, res, cfg, paths));
        }
        var badBase = orig.DeepClone().AsObject();
        badBase["config_bad_options"] = new JsonArray("ipv6");
        Assert.NotEmpty(Compare(badBase, res, cfg, paths));
    }

    [Fact]
    public void ErrorReference_WithOtherCode_FailsComparison()
    {
        var c = Files.ReadJson(Path.Combine(RepoPaths.ConformanceDir, "118-neg--unknown-placeholder.json"))!;
        using var paths = new TestPaths();
        var (res, cfg) = Run(c, paths);
        Assert.Empty(Compare(c, res, cfg, paths));
        ((JsonObject)c["expected"]!)["error"] = "placeholder_in_token";
        Assert.NotEmpty(Compare(c, res, cfg, paths));
    }

    internal static (GenerateResult Result, ZaprettConfig Cfg) Run(JsonObject c, TestPaths paths)
    {
        if (c["user_files"] is JsonObject uf)
            foreach (var (f, t) in uf)
                paths.WriteUser(f, R.Str(t) ?? "");
        var cfg = ConfigLoader.Normalize(new JsonObject { ["main"] = c["main"]!.DeepClone() });
        var engine = R.Str(c["engine"]) == "nfqws2" ? Engines.Winws2 : Engines.Winws;
        var store = new ItemStore(paths);
        var gen = new ArgsGenerator(paths, store);
        var text = R.Str(c["text"]);
        GenerateOptions opts;
        if (text != null)
        {
            var id = R.Str(c["item_id"]) ?? "user-test";
            opts = new GenerateOptions
            {
                Engine = engine, Text = text,
                Item = new StoreItem(id, Engines.ItemType(engine), id, "", "", "", null, null, [], "", "user", null, null, null, null, null),
            };
        }
        else
        {
            opts = new GenerateOptions { Engine = engine, Strategy = R.Str(c["strategy"]) };
        }
        return (gen.Build(cfg, opts), cfg);
    }

    /// <summary>Router path → Windows path of the sandbox (separators normalized inside the mapped path).</summary>
    internal static string MapRouterArg(string arg, TestPaths p)
    {
        var map = new (string Router, string Win)[]
        {
            ("/usr/share/zaprett/bundle/", p.BundleDir + "\\"),
            ("/usr/share/zaprett/guard/", Path.Combine(p.DataDir, "guard") + "\\"),
            ("/usr/share/zaprett/lua/", Path.Combine(p.Engine2Dir, "lua") + "\\"),
            ("/etc/zaprett/user/", p.UserDir + "\\"),
            ("/etc/zaprett/", p.InstalledDir + "\\"),
            ("/var/run/zaprett/", p.RunDir + "\\"),
        };
        foreach (var (router, win) in map)
        {
            var i = arg.IndexOf(router, StringComparison.Ordinal);
            if (i >= 0)
                return arg[..i] + win + arg[(i + router.Length)..].Replace('/', '\\');
        }
        return arg;
    }

    static List<string> StripRouterBase(IEnumerable<string> args)
    {
        var list = args.ToList();
        var n = 0;
        while (n < list.Count && RouterBasePrefixes.Any(b => list[n].StartsWith(b, StringComparison.Ordinal)))
            n++;
        return list.Skip(n).ToList();
    }

    internal static List<string> Compare(JsonObject c, GenerateResult res, ZaprettConfig cfg, TestPaths paths)
    {
        var d = new List<string>();
        var exp = (JsonObject)c["expected"]!;
        var bad = R.Strings(c["config_bad_options"]).Order(StringComparer.Ordinal).ToList();
        var mine = cfg.BadOptions.Order(StringComparer.Ordinal).ToList();
        if (!bad.SequenceEqual(mine))
            d.Add($"bad_options: router [{string.Join(",", bad)}] c# [{string.Join(",", mine)}]");
        var expOk = R.Bool(exp["ok"]);
        if (expOk != res.Ok)
        {
            d.Add($"ok: router {expOk} c# {res.Ok} ({res.Error}: {res.Message})");
            return d;
        }
        if (!expOk)
        {
            if (R.Str(exp["error"]) != res.Error)
                d.Add($"error: router {R.Str(exp["error"])} c# {res.Error}");
            return d;
        }
        var want = StripRouterBase(R.Strings(exp["args"])).Select(a => MapRouterArg(a, paths)).ToList();
        var got = res.StrategyArgs;
        for (var i = 0; i < Math.Max(want.Count, got.Count); i++)
        {
            var w = i < want.Count ? want[i] : "<none>";
            var g = i < got.Count ? got[i] : "<none>";
            if (w != g)
                d.Add($"arg[{i}]: router {w} | c# {g}");
        }
        var ports = (JsonObject)exp["ports"]!;
        if (!R.Strings(ports["tcp"]).SequenceEqual(res.TcpPorts))
            d.Add($"tcp: router {ports["tcp"]!.ToJsonString()} c# {string.Join(",", res.TcpPorts)}");
        if (!R.Strings(ports["udp"]).SequenceEqual(res.UdpPorts))
            d.Add($"udp: router {ports["udp"]!.ToJsonString()} c# {string.Join(",", res.UdpPorts)}");
        if (!R.Strings(exp["warnings"]).SequenceEqual(res.Warnings))
            d.Add($"warnings: router {exp["warnings"]!.ToJsonString()} c# {string.Join(",", res.Warnings)}");
        var ed = exp["details"] is JsonObject o ? o : [];
        if (!JsonNode.DeepEquals(ed, res.Details))
            d.Add($"details: router {ed.ToJsonString()} c# {res.Details.ToJsonString()}");
        return d;
    }
}
