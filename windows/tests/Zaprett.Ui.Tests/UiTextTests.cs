using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

public sealed class UiTextTests
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Error_known_code_uses_the_interface_text_not_the_service_message(string lang)
    {
        L.SetLanguage(lang);
        Assert.Equal(L.T("Err.item_active"), UiText.Error("item_active", "Стратегия «x» сейчас выбрана"));
        Assert.NotEqual("Err.item_active", L.T("Err.item_active"));
    }

    [Fact]
    public void Error_english_interface_uses_own_text_for_a_known_code()
    {
        L.SetLanguage("en");
        Assert.Equal("The item is in use. Turn it off first.", UiText.Error("item_active", "Стратегия «x» сейчас выбрана"));
    }

    [Fact]
    public void Error_unknown_code_without_message_names_the_code()
    {
        L.SetLanguage("en");
        Assert.Equal("Unknown error: weird_code", UiText.Error("weird_code", null));
        Assert.Equal("service text", UiText.Error("weird_code", "service text"));
    }

    [Fact]
    public void Error_access_denied_explains_the_rights()
    {
        L.SetLanguage("ru");
        Assert.Contains("zaprett Operators", UiText.Error("access_denied", "denied"), StringComparison.Ordinal);
        L.SetLanguage("zh-CN");
        Assert.Contains("zaprett Operators", UiText.Error("access_denied", null), StringComparison.Ordinal);
    }

    [Fact]
    public void Line_errors_explain_each_line_and_stop_at_20()
    {
        L.SetLanguage("en");
        var reply = Make.Json("""{"errors":[{"line":3,"value":"bad domain","reason":"bad_domain"},{"line":0,"reason":"too_large"}]}""");
        var lines = UiText.LineErrors(reply);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("Line 3: \"bad domain\"", lines[0], StringComparison.Ordinal);
        Assert.Equal(L.T("LineErr.too_large"), lines[1]);

        var many = Make.Json("{\"errors\":[" + string.Join(",", Enumerable.Range(1, 30).Select(i => $"{{\"line\":{i},\"value\":\"x\",\"reason\":\"bad_line\"}}")) + "]}");
        Assert.Equal(20, UiText.LineErrors(many).Count);
    }

    [Fact]
    public void Warning_bad_config_lists_the_options()
    {
        L.SetLanguage("en");
        var status = Make.Json("""{"details":{"bad_options":["monitor.interval","main.engine"]}}""");
        var w = UiText.Warning("bad_config", status);
        Assert.Contains("monitor.interval, main.engine", w.Text, StringComparison.Ordinal);
        Assert.Equal("settings", w.Action);
        Assert.False(w.IsInfo);
    }

    [Fact]
    public void Warning_info_codes_are_marked_as_info()
    {
        L.SetLanguage("en");
        Assert.True(UiText.Warning("dns_plain").IsInfo);
        Assert.True(UiText.Warning("test_running").IsInfo);
        Assert.False(UiText.Warning("not_running").IsInfo);
        Assert.Equal("restart", UiText.Warning("not_running").Action);
    }

    [Fact]
    public void Warning_unknown_code_is_shown_generically_not_dropped()
    {
        L.SetLanguage("en");
        var w = UiText.Warning("brand_new_code");
        Assert.Contains("brand_new_code", w.Title, StringComparison.Ordinal);
        Assert.Equal("diagnostics", w.Action);
    }

    [Theory]
    [InlineData("ok", Kind.Ok)]
    [InlineData("dns_spoof", Kind.Fail)]
    [InlineData("ip_block", Kind.Fail)]
    [InlineData("tls_block", Kind.Warn)]
    [InlineData("throttle", Kind.Warn)]
    [InlineData("http_block", Kind.Warn)]
    [InlineData("unknown", Kind.Info)]
    public void Verdicts_have_kinds_and_texts(string verdict, string kind)
    {
        L.SetLanguage("en");
        var v = UiText.Verdict(verdict);
        Assert.Equal(kind, v.Kind);
        Assert.NotEqual("Verdict." + verdict + ".Label", v.Label);
        Assert.False(string.IsNullOrWhiteSpace(v.Advice));
    }

    [Fact]
    public void Verdict_unknown_to_the_interface_asks_to_update()
    {
        L.SetLanguage("en");
        var v = UiText.Verdict("quantum_block");
        Assert.Equal("quantum_block", v.Label);
        Assert.Equal(L.T("Verdict.Other.Advice"), v.Advice);
    }

    [Fact]
    public void Target_error_includes_http_status()
    {
        L.SetLanguage("en");
        Assert.Equal("the server returned an HTTP error (HTTP 503)", UiText.TargetError(Make.Json("""{"error":"http_error","http_status":503}""")));
        Assert.Equal("", UiText.TargetError(Make.Json("""{"ok":true,"error":null}""")));
        Assert.Equal("new_code", UiText.TargetError(Make.Json("""{"error":"new_code"}""")));
    }

    [Theory]
    [InlineData(5, 5, Kind.Ok)]
    [InlineData(3, 5, Kind.Ok)]
    [InlineData(2, 5, Kind.Fail)]
    [InlineData(0, 5, Kind.Fail)]
    [InlineData(0, 0, Kind.None)]
    [InlineData(1, 2, Kind.Ok)]
    public void Check_fails_when_less_than_half_opened(long ok, long total, string kind) =>
        Assert.Equal(kind, UiText.CheckKind(ok, total));

    [Theory]
    [InlineData(3, 3, Kind.Ok)]
    [InlineData(2, 3, Kind.Warn)]
    [InlineData(0, 3, Kind.Fail)]
    [InlineData(0, 0, Kind.None)]
    public void Service_tile_kind(long ok, long total, string kind) => Assert.Equal(kind, UiText.ServiceKind(ok, total));

    [Fact]
    public void Formatting_bytes_durations_and_ms()
    {
        L.SetLanguage("en");
        Assert.Equal("512 B", UiText.Bytes(512));
        Assert.Equal("1.5 KB", UiText.Bytes(1536));
        Assert.Equal("2 MB", UiText.Bytes(2 * 1024 * 1024));
        Assert.Equal("—", UiText.Ms(0));
        Assert.Equal("420 ms", UiText.Ms(420));
        Assert.Equal("45 s", UiText.Duration(45));
        Assert.Equal("2 h 14 min", UiText.Duration(2 * 3600 + 14 * 60));
        Assert.Equal("3 d 1 h", UiText.Duration(3 * 86400 + 3600));
        Assert.Equal("never", UiText.Time(0));
        L.SetLanguage("ru");
        Assert.Equal("1,5 КБ", UiText.Bytes(1536));
        Assert.Equal("184 233", UiText.Number(184_233).Replace(' ', ' ').Replace(' ', ' '));
    }

    [Fact]
    public void Ago_is_relative_for_recent_moments()
    {
        L.SetLanguage("en");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.Equal("just now", UiText.Ago(1_800_000_000 - 10, now));
        Assert.Equal("5 min ago", UiText.Ago(1_800_000_000 - 300, now));
        Assert.Equal("3 h ago", UiText.Ago(1_800_000_000 - 3 * 3600, now));
        Assert.Equal("never", UiText.Ago(0, now));
    }

    [Fact]
    public void Job_names_and_states()
    {
        L.SetLanguage("ru");
        Assert.Equal("Автоподбор стратегии", UiText.JobName("test"));
        Assert.Equal("future-job", UiText.JobName("future-job"));
        Assert.Equal("отменено", UiText.JobState("cancelled"));
    }

    [Fact]
    public void Engine_names()
    {
        Assert.Equal("zapret (winws)", UiText.EngineName("winws"));
        Assert.Equal("zapret2 (winws2)", UiText.EngineName("nfqws2"));
    }

    [Fact]
    public void Test_mode_reasons_are_explained()
    {
        L.SetLanguage("en");
        Assert.Equal(L.T("Test.Reason.forced"), StrategiesViewModel.ModeReason("forced"));
        Assert.Equal("something_new", StrategiesViewModel.ModeReason("something_new"));
        Assert.Equal(L.T("Test.Reason.unknown"), StrategiesViewModel.ModeReason(null));
    }
}
