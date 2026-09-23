using System.Globalization;
using System.Text.Json.Nodes;

namespace Zaprett.Cli;

/// <summary>
/// CLI texts from the embedded resources Strings/&lt;lang&gt;.json (ARCHITECTURE-WIN §12.2): ru (default), en, zh-CN.
/// A key missing in a language falls back to Russian.
/// </summary>
public sealed class CliText
{
    public const string DefaultLanguage = "ru";
    public static readonly IReadOnlyList<string> Languages = ["ru", "en", "zh-CN"];

    private readonly JsonObject _strings;
    private readonly JsonObject _fallback;

    public string Language { get; }

    private CliText(string language, JsonObject strings, JsonObject fallback)
    {
        Language = language;
        _strings = strings;
        _fallback = fallback;
    }

    /// <summary>The language as given, or its canonical form (case-insensitive); null when not one of <see cref="Languages"/>.</summary>
    public static string? Normalize(string? lang) =>
        lang is null ? null : Languages.FirstOrDefault(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase));

    public static CliText Load(string? language)
    {
        string lang = Normalize(language) ?? DefaultLanguage;
        var fallback = Read(DefaultLanguage);
        return new CliText(lang, lang == DefaultLanguage ? fallback : Read(lang), fallback);
    }

    public static JsonObject Read(string lang)
    {
        using var s = typeof(CliText).Assembly.GetManifestResourceStream($"Zaprett.Cli.Strings.{lang}.json")
            ?? throw new InvalidOperationException("missing CLI strings for " + lang);
        return JsonNode.Parse(s) as JsonObject ?? throw new InvalidOperationException("bad CLI strings for " + lang);
    }

    public string this[string key] =>
        _strings[key]?.GetValue<string>() ?? _fallback[key]?.GetValue<string>() ?? key;

    public string Format(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, this[key], args);

    public string Usage => this["usage"];

    /// <summary>Usage error text from a parser error: "key" or "key:argument".</summary>
    public string UsageError(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return this["usage_error"];
        int colon = error.IndexOf(':', StringComparison.Ordinal);
        string detail = colon < 0 ? this[error] : Format(error[..colon], error[(colon + 1)..]);
        return Format("usage_error_detail", detail);
    }

    public string InputTooLarge(int limit) => Format("input_too_large", limit / 1024);
    public string InputNotJson => this["input_not_json"];
    public string InputNotJsonDetail(string detail) => Format("input_not_json_detail", detail);
    public string EmptyInput => this["empty_input"];
    public string NotAnObject => this["not_an_object"];
    public string Unavailable(string detail) => Format("unavailable", detail);
    public string Done => this["done"];
    public string Error => this["error"];
    public string Running => this["running"];
    public string Stopped => this["stopped"];
    public string Engine => this["engine"];
    public string Platform => this["platform"];
    public string ForeignWinDivert => this["foreign_windivert"];
    public string NoConflicts => this["no_conflicts"];
}
