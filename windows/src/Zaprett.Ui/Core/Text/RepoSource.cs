using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;

namespace Zaprett.Ui.Core.Text;

/// <summary>
/// repo.autoupdate of config.json: once a day the service downloads the index of the repository (repo.url) and
/// updates the strategies and lists installed from it. The switch says where the program goes: for the default
/// raw.githubusercontent.com/CherretGit/zaprett-repo/... that is "GitHub, repository CherretGit/zaprett-repo".
/// </summary>
public static class RepoSource
{
    /// <summary>The value when config.json has none (ConfigDefaults.Repo of the core: on).</summary>
    public const bool DefaultAutoupdate = true;

    public static bool Autoupdate(JsonObject? config) => config.Obj("repo").Bool("autoupdate", DefaultAutoupdate);

    /// <summary>"GitHub, repository owner/name" for a GitHub address, otherwise the host; null without an address.</summary>
    public static string? Where(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = uri.Host.ToLowerInvariant();
        if (host is "raw.githubusercontent.com" or "github.com" && parts.Length >= 2)
            return L.F("Repo.GitHub", parts[0] + "/" + parts[1]);
        return host;
    }

    /// <summary>The description of the switch: daily, where to (the default repository when the address is unknown).</summary>
    public static string Hint(JsonObject? config) =>
        L.F("Settings.RepoAutoupdateText", Where(config.Obj("repo").Str("url")) ?? L.F("Repo.GitHub", "CherretGit/zaprett-repo"));
}
