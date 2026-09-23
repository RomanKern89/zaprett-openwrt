using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zaprett.Core.Checks;
using Zaprett.Core.Lists;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Text;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Three languages (ARCHITECTURE-WIN §12.2): every resource key in ru, en, zh-CN with the same placeholders;
/// every key the code uses exists; no Russian text left in the code outside the resource table and the data files.</summary>
public sealed partial class LocalizationTests
{
    static JsonObject A(params (string K, JsonNode? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    static string CoreDir => Path.Combine(RepoPaths.Root, "windows", "src", "Zaprett.Core");

    static IEnumerable<string> CoreSources() =>
        Directory.GetFiles(CoreDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Contains("obj") && !f.Split(Path.DirectorySeparatorChar).Contains("bin"));

    [Fact]
    public void EveryKey_HasAllThreeLanguages()
    {
        Assert.Empty(T.Check(Strings.Table));
        Assert.True(Strings.Table.Count > 150);
    }

    [Fact]
    public void Check_FindsMissingAndBrokenTranslations()
    {
        // negative control: a removed translation and a lost placeholder must be reported
        var damaged = Strings.Table.ToDictionary(kv => kv.Key, kv => kv.Value);
        damaged["job.done"] = damaged["job.done"] with { Zh = "" };
        damaged["strategy.not_found"] = damaged["strategy.not_found"] with { En = "Strategy not found" };
        var bad = T.Check(damaged);
        Assert.Contains("job.done: zh-CN missing", bad);
        Assert.Contains("strategy.not_found: en placeholders differ", bad);
        Assert.Equal(2, bad.Count);
    }

    [GeneratedRegex("T\\.S\\(\"([a-z0-9_.]+)\"")]
    private static partial Regex KeyUse();

    [Fact]
    public void EveryKeyUsedInCode_Exists()
    {
        var used = CoreSources().SelectMany(f => KeyUse().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.'))
            .ToHashSet();
        Assert.True(used.Count > 150);
        Assert.Empty(used.Where(k => !Strings.Table.ContainsKey(k)));
        // keys built from codes
        foreach (var code in ProbeErrors.Codes.Append("too_large").Append("unknown"))
            Assert.True(Strings.Table.ContainsKey("probe.err." + code), code);
        foreach (var r in Diagnose.ReasonsWithText)
            Assert.True(Strings.Table.ContainsKey("diagnose.reason." + r), r);
        foreach (var e in Sources.ErrorCodes)
            Assert.True(Strings.Table.ContainsKey("src.err." + e), e);
    }

    [GeneratedRegex("\"[^\"\\n]*[А-Яа-яЁё][^\"\\n]*\"")]
    private static partial Regex RussianLiteral();

    [Fact]
    public void NoRussianText_OutsideResources()
    {
        // data with its own English fields (titles of default subscriptions and user lists) is allowed
        var allowed = new[] { "Strings.cs", "ConfigDefaults.cs", "ItemTypes.cs", "ItemStore.cs" };
        var found = new List<string>();
        foreach (var f in CoreSources().Where(f => !allowed.Contains(Path.GetFileName(f))))
            foreach (var (line, i) in File.ReadAllLines(f).Select((l, i) => (l, i)))
                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal) && RussianLiteral().IsMatch(line))
                    found.Add($"{Path.GetFileName(f)}:{i + 1}");
        Assert.Empty(found);
    }

    [Fact]
    public void Normalize_Languages()
    {
        Assert.Equal("zh-CN", T.Normalize("zh"));
        Assert.Equal("zh-CN", T.Normalize("ZH-cn"));
        Assert.Equal("en", T.Normalize("en-US"));
        Assert.Equal("ru", T.Normalize("ru-RU"));
        Assert.Null(T.Normalize("de"));
        Assert.Null(T.Normalize(""));
        Assert.Equal("nope.key", T.S("nope.key"));
    }

    [Fact]
    public async Task Messages_FollowLangArgumentAndUiLanguage()
    {
        using var h = new Harness();
        var ru = await h.Call("strategy.set", A(("id", "nope")));
        Assert.Equal("strategy_not_found", R.Error(ru));
        Assert.Contains("не найдена", R.Message(ru));
        var en = await h.Call("strategy.set", A(("id", "nope"), ("lang", "en")));
        Assert.Equal("strategy_not_found", R.Error(en));
        Assert.Equal("Strategy \"nope\" for engine winws not found", R.Message(en));
        var zh = await h.Call("strategy.set", A(("id", "nope"), ("lang", "zh-CN")));
        Assert.Equal("未找到引擎 winws 的策略“nope”", R.Message(zh));
        Assert.Equal("bad_value", R.Error(await h.Call("status", A(("lang", "de")))));

        Assert.True(R.IsOk(await h.Call("settings.set", A(("ui", new JsonObject { ["language"] = "zh-CN" })))));
        Assert.Equal("zh-CN", h.D.Context.Config.Load().Language);
        Assert.Equal("未找到策略“nope”", R.Message(await h.Call("strategy.show", A(("id", "nope")))));
        Assert.Contains("не найдена", R.Message(await h.Call("strategy.show", A(("id", "nope"), ("lang", "ru")))));
        Assert.Equal("bad_value", R.Error(await h.Call("settings.set", A(("ui", new JsonObject { ["language"] = "fr" })))));
        // "lang" is not written into config.json by settings.set
        await h.Call("settings.set", A(("main", new JsonObject { ["ipv6"] = true }), ("lang", "en")));
        Assert.DoesNotContain("\"lang\"", File.ReadAllText(h.F.Paths.ConfigFile));
    }

    [Fact]
    public async Task Job_ReportsInTheLanguageItWasStartedWith()
    {
        using var h = new Harness();
        h.F.Http.Probe = r => new Platform.ProbeResult(r.Url, true, 5, 999999, null, 200);
        var j = await h.Job("probe", A(("lang", "en")));
        var log = h.D.Context.Jobs.LogTail(100);
        Assert.Contains("Starting job probe", log);
        Assert.Contains("Checking services: 2, addresses: ", log);
        Assert.DoesNotContain("Проверка", log);
        Assert.EndsWith("addresses reachable", R.Message((JsonObject)j["result"]!));
        var z = await h.Job("probe", A(("lang", "zh-CN")));
        Assert.Contains("个地址中", R.Message((JsonObject)z["result"]!));
        Assert.Contains("启动任务 probe", h.D.Context.Jobs.LogTail(100));
    }
}
