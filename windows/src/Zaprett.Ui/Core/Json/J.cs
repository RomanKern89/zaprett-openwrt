using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zaprett.Ui.Core.Json;

/// <summary>
/// Tolerant readers for service answers: a missing field or a field of an unexpected type gives the default
/// instead of an exception, so an older or newer service never crashes the interface.
/// </summary>
public static class J
{
    public static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static JsonObject? Obj(this JsonNode? node, string key) => node.Get(key) as JsonObject;

    public static JsonArray Arr(this JsonNode? node, string key) => node.Get(key) as JsonArray ?? [];

    public static IEnumerable<JsonObject> Objs(this JsonNode? node, string key) => node.Arr(key).OfType<JsonObject>();

    public static string? Str(this JsonNode? node, string key)
    {
        if (node.Get(key) is not JsonValue v)
            return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static string Str(this JsonNode? node, string key, string fallback) => node.Str(key) ?? fallback;

    public static long Long(this JsonNode? node, string key, long fallback = 0)
    {
        if (node.Get(key) is not JsonValue v)
            return fallback;
        if (v.GetValueKind() == JsonValueKind.Number)
            return Number(v) is { } d ? (long)d : fallback;
        if (v.GetValueKind() == JsonValueKind.String &&
            long.TryParse(v.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        return fallback;
    }

    /// <summary>A number node as double. A parsed node answers TryGetValue&lt;double&gt;, but a node built from an int
    /// or long in code does not, so the JSON text is the common ground.</summary>
    public static double? Number(JsonValue v) =>
        v.GetValueKind() == JsonValueKind.Number &&
        double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d : null;

    public static double Double(this JsonNode? node, string key, double fallback = 0) =>
        node.Get(key) is JsonValue v && Number(v) is { } d ? d : fallback;

    public static int Int(this JsonNode? node, string key, int fallback = 0) =>
        (int)Math.Clamp(node.Long(key, fallback), int.MinValue, int.MaxValue);

    public static bool Bool(this JsonNode? node, string key, bool fallback = false)
    {
        if (node.Get(key) is not JsonValue v)
            return fallback;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Number(v) is { } d && d != 0,
            JsonValueKind.String => v.GetValue<string>() is "1" or "true",
            _ => fallback,
        };
    }

    public static bool IsOk(this JsonNode? node) => node.Bool("ok");

    /// <summary>A part of a "page" answer only when it is a successful answer itself ({ok:true,…}); a failed part is
    /// null, so the caller asks the method separately and gets its error as text.</summary>
    public static JsonObject? OkPart(this JsonNode? node, string key) => node.Obj(key) is { } p && p.IsOk() ? p : null;

    public static List<string> Strings(this JsonNode? node, string key) =>
        node.Arr(key).Select(x => x is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null)
            .Where(x => x != null).Select(x => x!).ToList();

    public static JsonArray ToJsonArray(this IEnumerable<string> items) => new(items.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    /// <summary>Deep copy (JsonNode can have only one parent).</summary>
    public static JsonObject Clone(this JsonObject o) => (JsonObject)o.DeepClone();
}
