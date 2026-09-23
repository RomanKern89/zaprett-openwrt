using System.Globalization;

namespace Zaprett.Core.Text;

/// <summary>Localized texts of the core (ARCHITECTURE-WIN §12.2): ru (default), en, zh-CN. Codes (error, warnings,
/// verdicts) never change; every human text comes from <see cref="Strings.Table"/> by key, in the language of the current
/// call (<see cref="Use"/>, set by the dispatcher from the argument "lang" or ui.language). The language flows into
/// background jobs with the execution context, so a job reports in the language it was started with.</summary>
public static class T
{
    public const string Ru = "ru";
    public const string En = "en";
    public const string Zh = "zh-CN";

    public static readonly IReadOnlyList<string> Languages = [Ru, En, Zh];

    static readonly AsyncLocal<string?> Current = new();

    public static string Lang => Current.Value ?? Ru;

    /// <summary>ru | en | zh-CN (also accepts "zh", "zh-cn", "en-US"...); null when not supported.</summary>
    public static string? Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return null;
        var l = lang.Trim().ToLowerInvariant();
        if (l == "ru" || l.StartsWith("ru-", StringComparison.Ordinal))
            return Ru;
        if (l == "en" || l.StartsWith("en-", StringComparison.Ordinal))
            return En;
        if (l is "zh" or "zh-cn" or "zh-hans" or "zh-sg" or "zh-hans-cn")
            return Zh;
        return null;
    }

    /// <summary>Sets the language for the rest of the current call (and the jobs it starts).</summary>
    public static void Use(string? lang) => Current.Value = Normalize(lang) ?? Ru;

    /// <summary>Text by key in the current language, with composite-format arguments {0}, {1}...</summary>
    public static string S(string key, params object?[] args)
    {
        if (!Strings.Table.TryGetValue(key, out var e))
            return key;
        var tpl = Lang switch
        {
            En => e.En,
            Zh => e.Zh,
            _ => e.Ru,
        };
        return args.Length == 0 ? tpl : string.Format(CultureInfo.InvariantCulture, tpl, args);
    }

    /// <summary>Problems of a resource table: an empty translation or a translation whose placeholders differ from the
    /// Russian one. Used by the tests (and by their negative control with a damaged table).</summary>
    public static List<string> Check(IReadOnlyDictionary<string, Entry> table)
    {
        var bad = new List<string>();
        foreach (var (k, e) in table)
        {
            var want = Holes(e.Ru);
            foreach (var (lang, s) in new[] { (Ru, e.Ru), (En, e.En), (Zh, e.Zh) })
            {
                if (string.IsNullOrWhiteSpace(s))
                    bad.Add($"{k}: {lang} missing");
                else if (!Holes(s).SetEquals(want))
                    bad.Add($"{k}: {lang} placeholders differ");
            }
        }
        return bad;
    }

    static HashSet<string> Holes(string s)
    {
        var h = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '{')
                continue;
            if (i + 1 < s.Length && s[i + 1] == '{')
            {
                i++;
                continue;
            }
            var j = s.IndexOf('}', i);
            if (j > i)
                h.Add(s[(i + 1)..j]);
        }
        return h;
    }

    public sealed record Entry(string Ru, string En, string Zh);
}
