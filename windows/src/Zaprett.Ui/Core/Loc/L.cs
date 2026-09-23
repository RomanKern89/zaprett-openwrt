using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zaprett.Ui.Core.Loc;

/// <summary>
/// Interface texts in Russian (the main language), English and simplified Chinese (embedded Strings/ru.json,
/// en.json, zh-CN.json; ARCHITECTURE-WIN §12.2). Russian is the default whatever the language of Windows is. A key
/// missing in the current language falls back to English, then to the key itself, so a gap is visible but never
/// crashes.
/// </summary>
public static class L
{
    public const string Russian = "ru";
    public const string English = "en";
    public const string Chinese = "zh-CN";

    /// <summary>Languages of the interface in the order of the switcher, with their own names.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
        [(Russian, "Русский"), (English, "English"), (Chinese, "简体中文")];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new();
    private static IReadOnlyDictionary<string, string> _current = Load(Russian);
    private static IReadOnlyDictionary<string, string> _english = Load(English);

    public static string Language { get; private set; } = Russian;

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru-RU");

    public static bool IsRussian => Language == Russian;

    /// <summary>A supported language code for any input: ru, en or zh-CN ("zh", "zh-Hans" → zh-CN); anything else
    /// (including null and the former "system") is Russian.</summary>
    public static string Normalize(string? lang) => lang switch
    {
        English or "en-US" or "en-GB" => English,
        Chinese or "zh" or "zh-Hans" or "zh-cn" or "zh-SG" => Chinese,
        _ => Russian,
    };

    public static void SetLanguage(string? lang)
    {
        var effective = Normalize(lang);
        _english = Load(English);
        _current = Load(effective);
        Language = effective;
        Culture = CultureInfo.GetCultureInfo(effective switch
        {
            English => "en-US",
            Chinese => "zh-CN",
            _ => "ru-RU",
        });
    }

    public static string T(string key) =>
        _current.TryGetValue(key, out var v) ? v : _english.TryGetValue(key, out var e) ? e : key;

    /// <summary>An enumeration in the interface language ("a, b" / "a、b"), never glued with a fixed separator.</summary>
    public static string List(IEnumerable<string?> items) => string.Join(T("Fmt.ListSep"), items);

    /// <summary>Formatted text; a broken placeholder in a translation shows the raw text instead of throwing.</summary>
    public static string F(string key, params object?[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template + " " + string.Join(", ", args);
        }
    }

    public static bool Has(string key) => _current.ContainsKey(key) || _english.ContainsKey(key);

    public static IReadOnlyDictionary<string, string> Table(string lang) => Load(lang);

    /// <summary>Bilingual metadata of presets and items (router contract §14.6): the Russian interface takes the
    /// field itself; Chinese takes "&lt;field&gt;_zh" when present; any non-Russian interface then takes
    /// "&lt;field&gt;_en"; otherwise the field itself.</summary>
    public static string? Pick(JsonNode? obj, string field)
    {
        if (obj is not JsonObject o)
            return null;
        if (!IsRussian)
        {
            var keys = Language == Chinese ? new[] { field + "_zh", field + "_en" } : [field + "_en"];
            foreach (var key in keys)
                if (Text(o, key) is { Length: > 0 } s)
                    return s;
        }
        return Text(o, field);
    }

    private static string? Text(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : null;

    private static IReadOnlyDictionary<string, string> Load(string lang)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(lang, out var t))
                return t;
            var asm = typeof(L).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Strings.{lang}.json", StringComparison.Ordinal));
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name)!;
                var root = JsonNode.Parse(s, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (root is JsonObject o)
                    foreach (var (k, v) in o)
                        if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String)
                            dict[k] = jv.GetValue<string>();
            }
            Tables[lang] = dict;
            return dict;
        }
    }
}
