using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zaprett.Core.Util;

/// <summary>Router-compatible answers: {"ok":true,...} and {"ok":false,"error":"code","message":"text",...}.</summary>
public static class R
{
    public static JsonObject Ok(JsonObject? extra = null)
    {
        var r = new JsonObject { ["ok"] = true };
        if (extra != null)
            foreach (var (k, v) in extra)
                r[k] = v?.DeepClone();
        return r;
    }

    public static JsonObject Fail(string code, string message, JsonObject? extra = null)
    {
        var r = new JsonObject { ["ok"] = false, ["error"] = code, ["message"] = message };
        if (extra != null)
            foreach (var (k, v) in extra)
                r[k] = v?.DeepClone();
        return r;
    }

    public static bool IsOk(JsonObject? r) => r != null && r["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string? Error(JsonObject? r) => Str(r?["error"]);

    public static string? Message(JsonObject? r) => Str(r?["message"]);

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool Bool(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    /// <summary>A JSON number as a whole number (fraction truncated); null for other kinds or out of range.</summary>
    public static long? Long(JsonNode? n)
    {
        var d = Number(n);
        return d is { } x && x >= long.MinValue && x <= long.MaxValue ? (long)decimal.Truncate(x) : null;
    }

    /// <summary>A JSON number whatever CLR type the node holds (parsed or created in code).</summary>
    public static decimal? Number(JsonNode? n)
    {
        if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
            return null;
        return decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static JsonArray Arr(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items)
            a.Add(s);
        return a;
    }

    public static JsonArray Arr(IEnumerable<JsonNode?> items)
    {
        var a = new JsonArray();
        foreach (var s in items)
            a.Add(s?.DeepClone());
        return a;
    }

    public static List<string> Strings(JsonNode? n)
    {
        var res = new List<string>();
        if (n is JsonArray a)
            foreach (var x in a)
                if (Str(x) is { } s)
                    res.Add(s);
        return res;
    }

    /// <summary>Parses JSON text into an object; null on any error.</summary>
    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Closed list of warnings (router contract §6.2, §14.4, §15.5): the core never emits others.</summary>
public static class Warnings
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "no_active_lists", "no_wan", "flow_offload_enabled", "nft_queue_missing", "engine_missing", "strategy_missing",
        "no_strategy", "generate_failed", "bad_config", "config_was_invalid", "list_missing", "source_not_downloaded",
        "profile_unfiltered", "wide_port_range", "empty_profile_removed", "strategy_option_ignored", "test_running",
        "not_running", "nft_not_applied", "ipv6_wan_unhandled", "low_memory", "monitor_degraded", "flowtable_failed",
        "game_filter_no_ipsets", "dns_plain",
    };
}
