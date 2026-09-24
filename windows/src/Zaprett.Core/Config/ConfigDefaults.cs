using System.Text.Json.Nodes;

namespace Zaprett.Core.Config;

/// <summary>Defaults of config.json (ARCHITECTURE-WIN §5) — the router UCI defaults without the router-only keys.</summary>
public static class ConfigDefaults
{
    public const string RepoUrl = "https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json";

    /// <summary>Minutes between monitor checks (router contract v1.3 §14.1).</summary>
    public static readonly IReadOnlyList<int> MonitorIntervals = [10, 15, 20, 30, 60];

    public static readonly IReadOnlyList<string> SourceTypes = ["list", "list_exclude", "ipset", "ipset_exclude"];

    public static JsonObject Main() => new()
    {
        ["enabled"] = false,
        ["autostart"] = false,
        ["engine"] = Engines.Winws,
        ["strategy"] = "strategy-general",
        ["strategy_winws2"] = "",
        ["list_mode"] = "whitelist",
        ["lists"] = new JsonArray("zaprett-youtube", "zaprett-discord", "user-hosts"),
        ["exclude_lists"] = new JsonArray("zaprett-exclude", "user-hosts-exclude"),
        ["ipsets"] = new JsonArray(),
        ["exclude_ipsets"] = new JsonArray("zaprett-exclude-ipset", "user-ipset-exclude"),
        ["ipv6"] = false,
        ["debug"] = false,
        ["watchdog"] = true,
        ["quic_block"] = false,
        ["game_filter"] = false,
        ["game_ports_tcp"] = "1024-65535",
        ["game_ports_udp"] = "1024-65535",
        ["network_filter"] = new JsonObject { ["mode"] = "all", ["ssids"] = new JsonArray(), ["skip_corporate"] = false },
        ["deleted_sources"] = new JsonArray(),
    };

    public static JsonObject Repo() => new() { ["url"] = RepoUrl, ["autoupdate"] = true, ["autoupdate_hour"] = 4 };

    public static JsonObject Test() => new() { ["timeout"] = 5, ["concurrency"] = 6, ["max_domains"] = 20, ["settle"] = 2 };

    public static JsonObject Monitor() => new()
    {
        ["enabled"] = true, ["interval"] = 30, ["threshold"] = 3, ["auto_repair"] = false, ["max_targets"] = 5, ["timeout"] = 8,
    };

    public static JsonObject Dns() => new() { ["mode"] = "system" };

    public static JsonObject Ui() => new() { ["language"] = "ru" };

    public static JsonObject Update() => new() { ["channel"] = "stable", ["check"] = true };

    /// <summary>Defaults of a subscription section (config.uc SOURCE_DEFAULTS).</summary>
    public static JsonObject Source() => new()
    {
        ["enabled"] = false, ["name"] = "", ["type"] = "list", ["url"] = "", ["interval_hours"] = 72, ["min_entries"] = 1,
        ["min_valid_ratio"] = 0.99, ["ram_mib"] = 0,
    };

    /// <summary>Subscriptions shipped by default (router contract v1.1 §4), all switched off.</summary>
    public static JsonObject Sources()
    {
        static JsonObject S(string name, string type, string url, int interval, int min, int ram) => new()
        {
            ["enabled"] = false, ["name"] = name, ["type"] = type, ["url"] = url, ["interval_hours"] = interval,
            ["min_entries"] = min, ["min_valid_ratio"] = 0.99, ["ram_mib"] = ram,
        };
        return new JsonObject
        {
            ["refilter_domains"] = S("Re:filter — заблокированные домены", "list",
                "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst", 72, 1000, 9),
            ["antifilter_allyouneed"] = S("antifilter — заблокированные IP-сети", "ipset",
                "https://antifilter.download/list/allyouneed.lst", 72, 1000, 2),
            ["cloudflare_v4"] = S("Cloudflare — IPv4-сети", "ipset", "https://www.cloudflare.com/ips-v4", 168, 5, 1),
            ["cloudflare_v6"] = S("Cloudflare — IPv6-сети", "ipset", "https://www.cloudflare.com/ips-v6", 168, 3, 1),
        };
    }

    public static readonly IReadOnlyList<string> DefaultSourceNames =
        ["refilter_domains", "antifilter_allyouneed", "cloudflare_v4", "cloudflare_v6"];

    /// <summary>The whole default document (what a fresh installation gets).</summary>
    public static JsonObject Document() => new()
    {
        ["schema"] = 1,
        ["main"] = Main(),
        ["repo"] = Repo(),
        ["test"] = Test(),
        ["monitor"] = Monitor(),
        ["dns"] = Dns(),
        ["sources"] = Sources(),
        ["update"] = Update(),
        ["ui"] = Ui(),
    };
}
