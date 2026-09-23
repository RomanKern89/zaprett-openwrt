using System.Text.RegularExpressions;
using Zaprett.Ui.Core.Loc;

namespace Zaprett.Ui.Tests;

public sealed partial class LocalizationTests
{
    [GeneratedRegex("""L\.(?:T|F)\("([A-Za-z0-9_.\-]+)"|L\.T\('([A-Za-z0-9_.\-]+)'\)""")]
    private static partial Regex KeyUse();

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();

    private static string UiSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Zaprett.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Zaprett.Ui");
    }

    /// <summary>Keys composed at run time from service codes (UiText, view models).</summary>
    public static IEnumerable<string> DynamicKeys()
    {
        string[] errors = ["service_unavailable", "access_denied", "job_busy", "busy", "test_running", "not_found", "strategy_not_found",
            "item_active", "item_in_use", "readonly_item", "no_job", "download_failed", "no_space", "dry_run_failed", "engine_missing",
            "engine_not_running", "invalid_entries", "too_large", "no_targets", "no_strategies", "repo_not_fetched", "preset_unavailable",
            "preset_item_missing", "presets_missing", "unknown_service", "unknown_variant", "bad_args", "uci_failed", "write_failed",
            "disabled", "bad_value", "sha256_mismatch", "source_update_failed", "cancelled", "usage", "unknown_method", "timeout",
            "bad_answer", "invalid_id", "update_failed", "no_update", "rpc_error", "too_many_subscriptions", "bad_id", "internal_error"];
        foreach (var e in errors)
            yield return "Err." + e;
        string[] warnings = ["no_active_lists", "engine_missing", "no_strategy", "strategy_missing", "generate_failed", "bad_config",
            "config_was_invalid", "list_missing", "source_not_downloaded", "profile_unfiltered", "wide_port_range", "empty_profile_removed",
            "strategy_option_ignored", "not_running", "test_running", "low_memory", "monitor_degraded", "game_filter_no_ipsets", "dns_plain",
            "ipv6_wan_unhandled", "windivert_foreign", "conflicts_found"];
        foreach (var w in warnings)
        {
            yield return $"Warn.{w}.Title";
            yield return $"Warn.{w}.Text";
        }
        foreach (var v in new[] { "ok", "dns_spoof", "ip_block", "tls_block", "throttle", "http_block", "unknown" })
        {
            yield return $"Verdict.{v}.Label";
            yield return $"Verdict.{v}.Advice";
        }
        foreach (var t in new[] { "too_small", "http_error", "timeout", "reset", "tls_cert", "tls_error", "connect_failed", "local_error", "failed" })
            yield return "TargetErr." + t;
        foreach (var j in new[] { "repo-fetch", "repo-install", "repo-upgrade", "repo-remove", "sources-update", "test", "probe", "dns-setup", "diagnose", "autoupdate", "update-install" })
            yield return "Job." + j;
        foreach (var s in new[] { "running", "done", "failed", "cancelled", "cancelling" })
            yield return "JobState." + s;
        foreach (var t in new[] { "nfqws", "winws", "nfqws2", "winws2", "list", "list_exclude", "ipset", "ipset_exclude", "bin", "lua_lib" })
            yield return "Type." + t;
        foreach (var s in new[] { "bundle", "repo", "user", "url" })
            yield return "Source." + s;
        foreach (var r in new[] { "bad_domain", "bad_cidr", "bad_line", "too_large", "not_text" })
            yield return "LineErr." + r;
        foreach (var r in new[] { "forced", "engine_not_running", "no_test_user", "qnum_out_of_range", "mark_conflict", "write_failed", "nft_rejected", "instance_failed", "unknown" })
            yield return "Test.Reason." + r;
        foreach (var id in new[] { "user-hosts", "user-hosts-exclude", "user-ipset", "user-ipset-exclude" })
            yield return "Lists.User." + id;
        foreach (var r in new[] { "Intro", "Services", "Conflicts", "Apply", "Fix", "Done" })
            yield return "Wizard.Rail." + r;
    }

    private static IEnumerable<string> UsedKeys()
    {
        var files = Directory.EnumerateFiles(UiSourceDir(), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (Match m in KeyUse().Matches(File.ReadAllText(f)))
                keys.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return keys;
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("zh-CN")]
    public void Every_language_has_the_same_keys_as_english(string lang)
    {
        var en = L.Table("en").Keys.ToHashSet();
        var other = L.Table(lang).Keys.ToHashSet();
        Assert.True(en.Count > 300, "en.json looks empty: " + en.Count);
        Assert.Empty(en.Except(other));
        Assert.Empty(other.Except(en));
    }

    [Fact]
    public void Chinese_texts_are_chinese_and_use_no_ascii_quotes()
    {
        var zh = L.Table("zh-CN");
        // every text except pure formats/names contains CJK characters
        var noCjk = zh.Where(kv => !kv.Value.Any(c => c is >= '\u4e00' and <= '\u9fff'))
            .Select(kv => kv.Key)
            .Where(k => !k.StartsWith("Fmt.", StringComparison.Ordinal) && k is not ("Settings.Version" or "Settings.SsidsHint"
                or "Settings.Dns.Doh" or "Type.lua_lib" or "Settings.Engine.Winws" or "Settings.Engine.Winws2"))
            .ToList();
        Assert.Empty(noCjk);
        Assert.Empty(zh.Where(kv => kv.Value.Contains('"')).Select(kv => kv.Key));
    }

    [Theory]
    [InlineData(null, "ru")]
    [InlineData("system", "ru")]
    [InlineData("de", "ru")]
    [InlineData("en", "en")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-CN", "zh-CN")]
    public void Russian_is_the_default_language(string? input, string expected) => Assert.Equal(expected, L.Normalize(input));

    [Theory]
    [InlineData("ru", "a, b", "x: 1")]
    [InlineData("en", "a, b", "x: 1")]
    [InlineData("zh-CN", "a、b", "x：1")]
    public void Enumerations_use_the_separators_of_the_language(string lang, string list, string count)
    {
        L.SetLanguage(lang);
        try
        {
            Assert.Equal(list, L.List(["a", "b"]));
            Assert.Equal(count, L.F("Fmt.CountItem", "x", 1));
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Theory]
    // all three translations present
    [InlineData("ru", """{"name":"Р","name_en":"E","name_zh":"中"}""", "Р")]
    [InlineData("en", """{"name":"Р","name_en":"E","name_zh":"中"}""", "E")]
    [InlineData("zh-CN", """{"name":"Р","name_en":"E","name_zh":"中"}""", "中")]
    // no Chinese: zh falls back to English
    [InlineData("zh-CN", """{"name":"Р","name_en":"E"}""", "E")]
    [InlineData("zh-CN", """{"name":"Р","name_en":"E","name_zh":""}""", "E")]
    // only the original
    [InlineData("en", """{"name":"Р"}""", "Р")]
    [InlineData("zh-CN", """{"name":"Р"}""", "Р")]
    [InlineData("en", """{"name":"Р","name_en":""}""", "Р")]
    // Chinese never leaks into the English or Russian interface
    [InlineData("en", """{"name":"Р","name_zh":"中"}""", "Р")]
    [InlineData("ru", """{"name":"Р","name_zh":"中"}""", "Р")]
    public void Pick_chooses_the_field_by_language(string lang, string json, string expected)
    {
        L.SetLanguage(lang);
        try
        {
            Assert.Equal(expected, L.Pick(Make.Json(json), "name"));
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Fact]
    public void Every_key_used_in_code_and_xaml_exists()
    {
        var used = UsedKeys().Where(k => !k.EndsWith('.')).ToList();
        Assert.True(used.Count > 250, "the source scan found too few keys: " + used.Count);
        var en = L.Table("en");
        Assert.Empty(used.Where(k => !en.ContainsKey(k)));
    }

    [Fact]
    public void Every_dynamic_key_exists()
    {
        var en = L.Table("en");
        Assert.Empty(DynamicKeys().Where(k => !en.ContainsKey(k)));
    }

    [Fact]
    public void Negative_control_a_missing_key_is_detected_and_falls_back_to_the_key()
    {
        Assert.DoesNotContain("Home.NoSuchKey", L.Table("en").Keys);
        L.SetLanguage("ru");
        Assert.Equal("Home.NoSuchKey", L.T("Home.NoSuchKey"));
        Assert.False(L.Has("Home.NoSuchKey"));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("zh-CN")]
    public void Placeholders_match_between_languages(string lang)
    {
        var en = L.Table("en");
        var other = L.Table(lang);
        var bad = en.Keys.Where(k => other.ContainsKey(k))
            .Where(k => !Placeholder().Matches(en[k]).Select(m => m.Value).Distinct().Order()
                .SequenceEqual(Placeholder().Matches(other[k]).Select(m => m.Value).Distinct().Order()))
            .ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Language_switch_changes_texts_and_culture()
    {
        L.SetLanguage("ru");
        Assert.Equal("Главная", L.T("Nav.Home"));
        Assert.True(L.IsRussian);
        L.SetLanguage("en");
        Assert.Equal("Home", L.T("Nav.Home"));
        Assert.Equal("en-US", L.Culture.Name);
        L.SetLanguage("zh-CN");
        Assert.Equal("主页", L.T("Nav.Home"));
        Assert.Equal("zh-CN", L.Culture.Name);
    }

    [Fact]
    public void Pick_prefers_english_metadata_only_for_the_english_interface()
    {
        var preset = Make.Json("""{"name":"Сайты за Cloudflare","name_en":"Sites behind Cloudflare"}""");
        L.SetLanguage("en");
        Assert.Equal("Sites behind Cloudflare", L.Pick(preset, "name"));
        L.SetLanguage("ru");
        Assert.Equal("Сайты за Cloudflare", L.Pick(preset, "name"));
        L.SetLanguage("en");
        Assert.Equal("Только имя", L.Pick(Make.Json("""{"name":"Только имя","name_en":""}"""), "name"));
        L.SetLanguage("zh-CN");
        Assert.Equal("Sites behind Cloudflare", L.Pick(preset, "name"));
        Assert.Equal("Cloudflare 后的网站", L.Pick(Make.Json("""{"name":"x","name_en":"y","name_zh":"Cloudflare 后的网站"}"""), "name"));
    }
}
