using System.Text.Json.Nodes;
using Zaprett.Core.Util;

namespace Zaprett.Core.Config;

/// <summary>The effective configuration as a config.json document (settings.get).</summary>
public static class ConfigDocument
{
    public static JsonObject From(ZaprettConfig c)
    {
        var sources = new JsonObject();
        foreach (var s in c.Sources)
            sources[s.Name] = new JsonObject
            {
                ["enabled"] = s.Enabled, ["name"] = s.Title, ["type"] = s.Type, ["url"] = s.Url, ["interval_hours"] = s.IntervalHours,
                ["min_entries"] = s.MinEntries, ["min_valid_ratio"] = s.MinValidRatio, ["ram_mib"] = s.RamMib,
            };
        return new JsonObject
        {
            ["schema"] = 1,
            ["main"] = new JsonObject
            {
                ["enabled"] = c.Enabled, ["autostart"] = c.Autostart, ["engine"] = c.Engine, ["strategy"] = c.Strategy, ["strategy_winws2"] = c.StrategyWinws2,
                ["list_mode"] = c.ListMode, ["lists"] = R.Arr(c.Lists), ["exclude_lists"] = R.Arr(c.ExcludeLists),
                ["ipsets"] = R.Arr(c.Ipsets), ["exclude_ipsets"] = R.Arr(c.ExcludeIpsets), ["ipv6"] = c.Ipv6, ["debug"] = c.Debug,
                ["watchdog"] = c.Watchdog, ["quic_block"] = c.QuicBlock, ["game_filter"] = c.GameFilter,
                ["game_ports_tcp"] = c.GamePortsTcp, ["game_ports_udp"] = c.GamePortsUdp,
                ["network_filter"] = new JsonObject
                {
                    ["mode"] = c.NetworkFilter.Mode, ["ssids"] = R.Arr(c.NetworkFilter.Ssids), ["skip_corporate"] = c.NetworkFilter.SkipCorporate,
                },
                ["deleted_sources"] = R.Arr(c.DeletedSources),
                ["debug_until"] = c.DebugUntil > 0 ? c.DebugUntil : null,
            },
            ["repo"] = new JsonObject { ["url"] = c.Repo.Url, ["autoupdate"] = c.Repo.Autoupdate, ["autoupdate_hour"] = c.Repo.AutoupdateHour },
            ["test"] = new JsonObject
            {
                ["timeout"] = c.Test.Timeout, ["concurrency"] = c.Test.Concurrency, ["max_domains"] = c.Test.MaxDomains, ["settle"] = c.Test.Settle,
            },
            ["monitor"] = new JsonObject
            {
                ["enabled"] = c.Monitor.Enabled, ["interval"] = c.Monitor.Interval, ["threshold"] = c.Monitor.Threshold,
                ["auto_repair"] = c.Monitor.AutoRepair, ["max_targets"] = c.Monitor.MaxTargets, ["timeout"] = c.Monitor.Timeout,
            },
            ["dns"] = new JsonObject { ["mode"] = c.Dns.Mode },
            ["sources"] = sources,
            ["update"] = new JsonObject { ["channel"] = c.Update.Channel, ["check"] = c.Update.Check },
            ["ui"] = new JsonObject { ["language"] = c.Language },
        };
    }
}
