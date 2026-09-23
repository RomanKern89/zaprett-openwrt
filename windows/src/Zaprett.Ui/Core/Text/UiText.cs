using System.Globalization;
using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;

namespace Zaprett.Ui.Core.Text;

/// <summary>Colour meaning of a state; the view maps it to theme brushes (StateColor).</summary>
public static class Kind
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Info = "info";
    public const string None = "none";
}

/// <summary>A warning of "status" explained for a person: what happened, what to do, where to go.</summary>
public sealed record WarningText(string Code, string Title, string Text, string? Action, bool IsInfo);

/// <summary>A verdict of "diagnose" (router contract §15.4).</summary>
public sealed record VerdictText(string Verdict, string Label, string Kind, string Advice);

/// <summary>
/// Texts for codes that come from the service: errors, warnings, verdicts, reasons of failed checks, job names,
/// plus number/time formatting. Everything returns plain text; the view never interprets it as markup.
/// </summary>
public static class UiText
{
    /// <summary>Warnings that only inform (router contract §14.4, §15.5): nothing is broken.</summary>
    public static readonly IReadOnlySet<string> InfoWarnings =
        new HashSet<string>(StringComparer.Ordinal) { "empty_profile_removed", "strategy_option_ignored", "test_running", "dns_plain" };

    private static readonly HashSet<string> KnownErrors = new(StringComparer.Ordinal)
    {
        "service_unavailable", "access_denied", "job_busy", "busy", "test_running", "not_found", "strategy_not_found",
        "item_active", "item_in_use", "readonly_item", "no_job", "download_failed", "no_space", "dry_run_failed",
        "engine_missing", "engine_not_running", "invalid_entries", "too_large", "no_targets", "no_strategies",
        "repo_not_fetched", "preset_unavailable", "preset_item_missing", "presets_missing", "unknown_service",
        "unknown_variant", "bad_args", "uci_failed", "write_failed", "disabled", "bad_value", "sha256_mismatch",
        "source_update_failed", "cancelled", "usage", "unknown_method", "timeout", "bad_answer", "invalid_id",
        "update_failed", "no_update", "rpc_error", "too_many_subscriptions", "bad_id", "internal_error",
    };

    public static bool IsKnownError(string code) => KnownErrors.Contains(code);

    /// <summary>
    /// Human text for an error answer {ok:false, error, message} (ARCHITECTURE-WIN §12.2): the text of the code from
    /// the resources of the interface language; the service message (already in that language, "lang" is sent with
    /// every call) only for a code the interface does not know.
    /// </summary>
    public static string Error(string? code, string? serviceMessage)
    {
        var msg = serviceMessage?.Trim();
        if (!string.IsNullOrEmpty(code) && KnownErrors.Contains(code))
            return L.T("Err." + code);
        if (!string.IsNullOrEmpty(msg))
            return msg;
        return L.F("Err.Unknown", string.IsNullOrEmpty(code) ? "?" : code);
    }

    /// <summary>Explanations of invalid lines of "user.set" ({errors:[{line, value, reason}]}), at most 20.</summary>
    public static IReadOnlyList<string> LineErrors(JsonNode? reply)
    {
        var list = new List<string>();
        foreach (var e in reply.Objs("errors").Take(20))
        {
            var reason = e.Str("reason") ?? "";
            var text = reason is "bad_domain" or "bad_cidr" or "bad_line" or "too_large" or "not_text"
                ? L.T("LineErr." + reason) : reason;
            var line = e.Long("line");
            list.Add(line > 0 ? L.F("LineErr.Line", line, e.Str("value") ?? "", text) : text);
        }
        return list;
    }

    public static WarningText Warning(string code, JsonNode? status = null)
    {
        var isInfo = InfoWarnings.Contains(code);
        var key = "Warn." + code;
        if (!L.Has(key + ".Title"))
            return new WarningText(code, L.F("Warn.Unknown.Title", code), L.T("Warn.Unknown.Text"), "diagnostics", isInfo);

        var text = L.T(key + ".Text");
        var details = status.Obj("details");
        if (code == "bad_config")
        {
            var bad = details.Strings("bad_options");
            if (bad.Count > 0)
                text = L.F("Warn.bad_config.TextWith", L.List(bad));
        }
        else if (code == "generate_failed")
        {
            var m = details.Obj("generate").Str("message");
            if (!string.IsNullOrWhiteSpace(m))
                text = L.F("Warn.generate_failed.TextWith", m);
        }
        return new WarningText(code, L.T(key + ".Title"), text, WarningAction(code), isInfo);
    }

    private static string? WarningAction(string code) => code switch
    {
        "no_active_lists" or "list_missing" or "source_not_downloaded" or "low_memory" or "game_filter_no_ipsets" => "lists",
        "no_strategy" or "strategy_missing" or "profile_unfiltered" or "wide_port_range" or "test_running" or "monitor_degraded" => "strategies",
        "bad_config" or "ipv6_wan_unhandled" or "dns_plain" => "settings",
        "generate_failed" or "config_was_invalid" or "conflicts_found" or "windivert_foreign" => "diagnostics",
        "not_running" => "restart",
        _ => null,
    };

    /// <summary>Verdicts of "diagnose" (§15.4); an unknown one is shown neutrally with a hint to update.</summary>
    public static VerdictText Verdict(string? verdict)
    {
        var v = verdict ?? "unknown";
        var kind = v switch
        {
            "ok" => Kind.Ok,
            "dns_spoof" or "ip_block" => Kind.Fail,
            "tls_block" or "throttle" or "http_block" => Kind.Warn,
            _ => Kind.Info,
        };
        if (!L.Has("Verdict." + v + ".Label"))
            return new VerdictText(v, v, Kind.Info, L.T("Verdict.Other.Advice"));
        return new VerdictText(v, L.T("Verdict." + v + ".Label"), kind, L.T("Verdict." + v + ".Advice"));
    }

    /// <summary>Order of verdicts for "which is the most frequent" ties (§15.8 item 4).</summary>
    public static readonly string[] VerdictOrder = ["ok", "dns_spoof", "ip_block", "tls_block", "throttle", "http_block", "unknown"];

    /// <summary>Reason of a failed check of one address (probe, tester), closed list of §14.3.</summary>
    public static string TargetError(JsonNode? target)
    {
        var code = target.Str("error");
        if (string.IsNullOrEmpty(code))
            return "";
        var text = code is "too_small" or "http_error" or "timeout" or "reset" or "tls_cert" or "tls_error"
            or "connect_failed" or "local_error" or "failed"
            ? L.T("TargetErr." + code) : code;
        var http = target.Long("http_status");
        return http > 0 ? $"{text} (HTTP {http})" : text;
    }

    public static string JobName(string? name) => name switch
    {
        "repo-fetch" or "repo-install" or "repo-upgrade" or "repo-remove" or "sources-update" or "test" or "probe"
            or "dns-setup" or "diagnose" or "autoupdate" or "update-install" => L.T("Job." + name),
        null or "" => "",
        _ => name,
    };

    public static string JobState(string? state) => state switch
    {
        "running" or "done" or "failed" or "cancelled" or "cancelling" => L.T("JobState." + state),
        _ => state ?? "",
    };

    public static string Severity(string? severity) => severity switch
    {
        "block" => L.T("Conflict.Block"),
        "warn" => L.T("Conflict.Warn"),
        _ => L.T("Conflict.Info"),
    };

    public static string SeverityKind(string? severity) => severity switch
    {
        "block" => Kind.Fail,
        "warn" => Kind.Warn,
        _ => Kind.Info,
    };

    public static string TypeLabel(string? type) => type switch
    {
        "nfqws" or "winws" or "nfqws2" or "winws2" or "list" or "list_exclude" or "ipset" or "ipset_exclude" or "bin" or "lua_lib"
            => L.T("Type." + type),
        _ => type ?? "",
    };

    public static string SourceLabel(string? source) => source switch
    {
        "bundle" or "repo" or "user" or "url" => L.T("Source." + source),
        _ => source ?? "",
    };

    public static string EngineName(string? engine) => engine switch
    {
        "winws2" or "nfqws2" => "zapret2 (winws2)",
        "winws" or "nfqws" => "zapret (winws)",
        null or "" => L.T("Common.Unknown"),
        _ => engine,
    };

    // ---------- formatting ----------

    public static string Ms(long ms) => ms > 0 ? L.F("Fmt.Ms", ms) : "—";

    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
            return L.F("Fmt.B", 0);
        if (bytes < 1024)
            return L.F("Fmt.B", bytes);
        if (bytes < 1024 * 1024)
            return L.F("Fmt.KiB", Math.Round(bytes / 1024.0, 1));
        return L.F("Fmt.MiB", Math.Round(bytes / 1048576.0, 1));
    }

    public static string Number(long n) => n.ToString("N0", L.Culture);

    /// <summary>Largest Unix time in seconds that DateTimeOffset accepts (9999-12-31).</summary>
    private const long MaxEpoch = 253_402_300_799;

    /// <summary>Unix seconds; a value in milliseconds is recognised and converted, nonsense gives null.</summary>
    private static DateTimeOffset? FromEpoch(long epoch)
    {
        if (epoch > MaxEpoch)
            epoch /= 1000;
        return epoch is > 0 and <= MaxEpoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
    }

    public static string Time(long epoch) =>
        FromEpoch(epoch) is { } t ? t.ToLocalTime().ToString("g", L.Culture) : L.T("Fmt.Never");

    /// <summary>"just now", "5 min ago", "3 h ago", or the date for older moments.</summary>
    public static string Ago(long epoch, DateTimeOffset now)
    {
        if (FromEpoch(epoch) is not { } t)
            return L.T("Fmt.Never");
        var diff = now - t;
        if (diff.TotalSeconds < 60)
            return L.T("Fmt.JustNow");
        if (diff.TotalMinutes < 60)
            return L.F("Fmt.MinAgo", (int)diff.TotalMinutes);
        if (diff.TotalHours < 24)
            return L.F("Fmt.HoursAgo", (int)diff.TotalHours);
        return Time(epoch);
    }

    public static string Duration(long seconds)
    {
        seconds = Math.Clamp(seconds, 0, 3650L * 86400);
        if (seconds < 60)
            return L.F("Fmt.Sec", Math.Max(0, seconds));
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours < 1)
            return L.F("Fmt.Min", (int)span.TotalMinutes);
        if (span.TotalDays < 1)
            return L.F("Fmt.HourMin", (int)span.TotalHours, span.Minutes);
        return L.F("Fmt.DayHour", (int)span.TotalDays, span.Hours);
    }

    public static string Percent(double ratio) => (Math.Clamp(ratio, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    /// <summary>A check fails when less than half of its targets opened; without targets it does not count
    /// (router contract §14.1).</summary>
    public static string CheckKind(long ok, long total) => total <= 0 ? Kind.None : ok * 2 < total ? Kind.Fail : Kind.Ok;

    /// <summary>State of one service tile: all opened, some, none, nothing to check.</summary>
    public static string ServiceKind(long ok, long total) =>
        total <= 0 ? Kind.None : ok >= total ? Kind.Ok : ok > 0 ? Kind.Warn : Kind.Fail;
}
