using System.Text.RegularExpressions;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>Client-side checks that mirror the service rules, so obvious mistakes are shown before a call.</summary>
public static partial class Validation
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,96}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex SourceNameRegex();

    /// <summary>Ids of items: ^[A-Za-z0-9._-]{1,96}$ (router contract §6.2).</summary>
    public static bool IsId(string? id) => id != null && IdRegex().IsMatch(id);

    /// <summary>Own strategies must be named "user-…" (router contract §6.2).</summary>
    public static bool IsUserStrategyId(string? id) => IsId(id) && id!.StartsWith("user-", StringComparison.Ordinal) && id.Length > 5;

    public static bool IsDomain(string? line) => line != null && line.Length <= 253 && DomainRegex().IsMatch(line.Trim());

    public static bool IsSourceName(string? name) => name != null && SourceNameRegex().IsMatch(name);

    public static bool IsHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);

    /// <summary>Turns a free name into a valid own strategy id: "My YouTube" → "user-my-youtube".</summary>
    public static string ToUserStrategyId(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-');
        if (slug.StartsWith("user-", StringComparison.Ordinal))
            slug = slug[5..];
        slug = slug.Length == 0 ? "strategy" : slug;
        var id = "user-" + slug;
        return id.Length > 96 ? id[..96] : id;
    }

    [GeneratedRegex(@"(https?://)[^\s/@?#""']+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"(https?://[^\s?#""']+)\?[^\s#""']*", RegexOptions.IgnoreCase)]
    private static partial Regex Query();

    /// <summary>Hides the user info and the query of URLs (tokens of subscriptions live there) — ARCHITECTURE-WIN W-8.
    /// A token inside the path cannot be told from an ordinary path and stays.</summary>
    public static string MaskUrls(string text) => Query().Replace(UserInfo().Replace(text, "$1…@"), "$1?…");
}
