using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;

namespace Zaprett.Core.Config;

/// <summary>Pure normalization of the raw config.json document into <see cref="ZaprettConfig"/> (port of config.uc
/// normalize): an invalid value falls back to its default and its name goes to bad_options (warning bad_config).
/// A missing key takes the default silently. Boolean values accept JSON booleans and the UCI forms "1"/"0"/"true"/...,
/// numbers accept JSON numbers and digit strings, so a router-style section normalizes the same way.</summary>
public static class ConfigLoader
{
    public static ZaprettConfig Normalize(JsonObject? doc)
    {
        doc ??= [];
        var bad = new List<string>();
        var main = doc["main"] as JsonObject;
        var repo = doc["repo"] as JsonObject;
        var test = doc["test"] as JsonObject;
        var monitorPresent = doc.ContainsKey("monitor") ? doc["monitor"] is JsonObject : true;
        var monitor = doc["monitor"] as JsonObject;
        var dns = doc["dns"] as JsonObject;
        var update = doc["update"] as JsonObject;
        var ui = doc["ui"] as JsonObject;

        var dMain = ConfigDefaults.Main();
        var r = new Reader(bad);

        var engine = r.Enum(main, dMain, "engine", "", [Engines.Winws, Engines.Winws2]);
        var enabled = r.Bool(main, dMain, "enabled", "");
        // a configuration from before main.autostart behaved as autostart = enabled: it keeps doing so
        var autostart = enabled;
        if (main?["autostart"] is { } an)
        {
            if (Reader.AsBool(an) is { } ab)
                autostart = ab;
            else
                bad.Add("autostart");
        }
        var cfg = new ZaprettConfig
        {
            Enabled = enabled,
            Autostart = autostart,
            Engine = engine,
            Strategy = r.StrategyId(main, dMain, "strategy"),
            StrategyWinws2 = r.StrategyId(main, dMain, "strategy_winws2"),
            ListMode = r.Enum(main, dMain, "list_mode", "", ["whitelist", "blacklist"]),
            Lists = r.Ids(main, dMain, "lists"),
            ExcludeLists = r.Ids(main, dMain, "exclude_lists"),
            Ipsets = r.Ids(main, dMain, "ipsets"),
            ExcludeIpsets = r.Ids(main, dMain, "exclude_ipsets"),
            Ipv6 = r.Bool(main, dMain, "ipv6", ""),
            Debug = r.Bool(main, dMain, "debug", ""),
            Watchdog = r.Bool(main, dMain, "watchdog", ""),
            QuicBlock = r.Bool(main, dMain, "quic_block", ""),
            GameFilter = r.Bool(main, dMain, "game_filter", ""),
            GamePortsTcp = r.GamePorts(main, dMain, "game_ports_tcp"),
            GamePortsUdp = r.GamePorts(main, dMain, "game_ports_udp"),
            NetworkFilter = r.NetworkFilter(main?["network_filter"]),
            DeletedSources = r.Names(main, dMain, "deleted_sources"),
            DebugUntil = R.Long(main?["debug_until"]) is { } du && du > 0 ? du : 0,
            Language = r.Enum(ui, ConfigDefaults.Ui(), "language", "ui.", ["ru", "en", "zh-CN"]),
            Repo = new RepoConfig(
                r.Url(repo),
                r.Bool(repo, ConfigDefaults.Repo(), "autoupdate", ""),
                r.Uint(repo, ConfigDefaults.Repo(), "autoupdate_hour", "", 0, 23)),
            Test = new TestConfig(
                r.Uint(test, ConfigDefaults.Test(), "timeout", "", 1, 60),
                r.Uint(test, ConfigDefaults.Test(), "concurrency", "", 1, 16),
                r.Uint(test, ConfigDefaults.Test(), "max_domains", "", 0, 500),
                r.Uint(test, ConfigDefaults.Test(), "settle", "", 0, 30)),
            Monitor = r.Monitor(monitor, monitorPresent),
            Dns = new DnsConfig(r.Enum(dns, ConfigDefaults.Dns(), "mode", "dns.", ["system", "doh"])),
            Update = new UpdateConfig(
                r.Enum(update, ConfigDefaults.Update(), "channel", "update.", ["stable", "beta"]),
                r.Bool(update, ConfigDefaults.Update(), "check", "update.")),
            Sources = NormalizeSources(doc["sources"] as JsonObject ?? (doc.ContainsKey("sources") ? [] : ConfigDefaults.Sources())),
        };
        return cfg with { BadOptions = bad.Distinct().ToList() };
    }

    public static IReadOnlyList<SourceConfig> NormalizeSources(JsonObject sources)
    {
        var res = new List<SourceConfig>();
        foreach (var (name, node) in sources)
            res.Add(NormalizeSource(name, node as JsonObject));
        return res;
    }

    /// <summary>Typed subscription from its raw section (config.uc normalize_source). Valid=false when the name, type or
    /// URL is unusable.</summary>
    public static SourceConfig NormalizeSource(string name, JsonObject? raw)
    {
        var bad = new List<string>();
        var d = ConfigDefaults.Source();
        JsonNode? Get(string k) => raw != null && raw[k] != null ? raw[k] : d[k];
        var title = R.Str(Get("name"))?.Trim() ?? "";
        if (title.Length > 128)
            title = title[..128];
        var type = R.Str(Get("type")) ?? "";
        var url = R.Str(Get("url")) ?? "";
        int U(string k, int min, int max)
        {
            var v = Reader.AsUint(Get(k), min, max);
            if (v == null)
            {
                bad.Add(k);
                return (int)R.Long(d[k])!;
            }
            return v.Value;
        }
        var interval = U("interval_hours", 1, 8760);
        var minEntries = U("min_entries", 0, 100000000);
        var ratio = Reader.AsRatio(Get("min_valid_ratio"));
        if (ratio == null)
        {
            bad.Add("min_valid_ratio");
            ratio = 0.99;
        }
        var ram = U("ram_mib", 0, 65536);
        if (!Validate.SourceNameValid(name))
            bad.Add("section");
        if (!ConfigDefaults.SourceTypes.Contains(type))
            bad.Add("type");
        if (!Validate.HttpsUrlValid(url))
            bad.Add("url");
        if (title.Length == 0)
            title = name;
        var valid = !(bad.Contains("section") || bad.Contains("type") || bad.Contains("url"));
        return new SourceConfig(name, Reader.AsBool(Get("enabled")) == true, title, type, url, interval, minEntries,
            ratio.Value, ram, valid, bad);
    }

    sealed class Reader(List<string> bad)
    {
        static JsonNode? Pick(JsonObject? raw, JsonObject defaults, string name) =>
            raw != null && raw.ContainsKey(name) && raw[name] != null ? raw[name] : defaults[name];

        public static bool? AsBool(JsonNode? n)
        {
            if (n is not JsonValue v)
                return null;
            switch (v.GetValueKind())
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return R.Long(v) switch { 1 => true, 0 => false, _ => null };
                case JsonValueKind.String:
                    return v.GetValue<string>() switch
                    {
                        "1" or "true" or "on" or "yes" => true,
                        "0" or "false" or "off" or "no" or "" => false,
                        _ => null,
                    };
                default:
                    return null;
            }
        }

        public static int? AsUint(JsonNode? n, int min, int max)
        {
            if (n is not JsonValue v)
                return null;
            if (v.GetValueKind() == JsonValueKind.Number)
            {
                var d = R.Number(v);
                if (d == null || d != decimal.Truncate(d.Value) || d < min || d > max)
                    return null;
                return (int)d.Value;
            }
            return v.GetValueKind() == JsonValueKind.String ? Validate.ParseUint(v.GetValue<string>(), min, max) : null;
        }

        public static double? AsRatio(JsonNode? n)
        {
            if (n is not JsonValue v)
                return null;
            double x;
            if (v.GetValueKind() == JsonValueKind.Number)
                x = (double)R.Number(v)!.Value;
            else if (v.GetValueKind() != JsonValueKind.String ||
                     !double.TryParse(v.GetValue<string>(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out x))
                return null;
            return x is >= 0 and <= 1 ? x : null;
        }

        public bool Bool(JsonObject? raw, JsonObject defaults, string name, string prefix)
        {
            var b = AsBool(Pick(raw, defaults, name));
            if (b != null)
                return b.Value;
            bad.Add(prefix + name);
            return AsBool(defaults[name]) == true;
        }

        public int Uint(JsonObject? raw, JsonObject defaults, string name, string prefix, int min, int max)
        {
            var v = AsUint(Pick(raw, defaults, name), min, max);
            if (v != null)
                return v.Value;
            bad.Add(prefix + name);
            return (int)R.Long(defaults[name])!;
        }

        public string Enum(JsonObject? raw, JsonObject defaults, string name, string prefix, string[] allowed)
        {
            var s = R.Str(Pick(raw, defaults, name));
            if (s != null && allowed.Contains(s))
                return s;
            bad.Add(prefix + name);
            return R.Str(defaults[name])!;
        }

        public string StrategyId(JsonObject? raw, JsonObject defaults, string name)
        {
            var s = R.Str(Pick(raw, defaults, name));
            if (s == null)
            {
                bad.Add(name);
                return "";
            }
            if (s.Length > 0 && !Validate.IsId(s))
            {
                bad.Add(name);
                return "";
            }
            return s;
        }

        static List<string> AsList(JsonNode? n)
        {
            var res = new List<string>();
            IEnumerable<JsonNode?> items = n is JsonArray a ? a : n == null ? [] : [n];
            foreach (var x in items)
            {
                if (x is not JsonValue v)
                    continue;
                var s = (v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString()).Trim();
                if (s.Length > 0 && !res.Contains(s))
                    res.Add(s);
            }
            return res;
        }

        public List<string> Ids(JsonObject? raw, JsonObject defaults, string name)
        {
            var res = new List<string>();
            foreach (var x in AsList(Pick(raw, defaults, name)))
            {
                if (Validate.IsId(x))
                    res.Add(x);
                else
                    bad.Add(name);
            }
            return res;
        }

        public List<string> Names(JsonObject? raw, JsonObject defaults, string name) =>
            AsList(Pick(raw, defaults, name)).Where(Validate.SourceNameValid).ToList();

        public string GamePorts(JsonObject? raw, JsonObject defaults, string name)
        {
            var s = R.Str(Pick(raw, defaults, name));
            if (s == "")
                return "";
            var p = Validate.GamePorts(s);
            if (p != null)
                return p;
            bad.Add(name);
            return R.Str(defaults[name])!;
        }

        public string Url(JsonObject? repo)
        {
            var u = R.Str(Pick(repo, ConfigDefaults.Repo(), "url"));
            if (Validate.HttpsUrlValid(u))
                return u!;
            bad.Add("url");
            return ConfigDefaults.RepoUrl;
        }

        public NetworkFilterConfig NetworkFilter(JsonNode? node)
        {
            var raw = node as JsonObject;
            var d = (JsonObject)ConfigDefaults.Main()["network_filter"]!;
            if (node != null && raw == null)
                bad.Add("network_filter");
            var mode = Enum(raw, d, "mode", "network_filter.", ["all", "ssids"]);
            var ssids = new List<string>();
            foreach (var s in AsList(Pick(raw, d, "ssids")))
            {
                if (SsidValid(s))
                    ssids.Add(s);
                else
                    bad.Add("network_filter.ssids");
            }
            var skip = Bool(raw, d, "skip_corporate", "network_filter.");
            if (mode == "ssids" && ssids.Count == 0)
            {
                bad.Add("network_filter.ssids");
                mode = "all";
            }
            return new NetworkFilterConfig(mode, ssids, skip);
        }

        public MonitorConfig Monitor(JsonObject? raw, bool present)
        {
            var d = ConfigDefaults.Monitor();
            var m = new MonitorConfig(
                present,
                present && Bool(raw, d, "enabled", "monitor."),
                Uint(raw, d, "interval", "monitor.", 1, 60),
                Uint(raw, d, "threshold", "monitor.", 1, 20),
                Bool(raw, d, "auto_repair", "monitor."),
                Uint(raw, d, "max_targets", "monitor.", 1, 20),
                Uint(raw, d, "timeout", "monitor.", 2, 30));
            if (!ConfigDefaults.MonitorIntervals.Contains(m.Interval))
            {
                bad.Add("monitor.interval");
                m = m with { Interval = 30 };
            }
            return m;
        }
    }

    /// <summary>SSID for --ssid-filter: 1..32 characters, no control characters and no comma (the list separator).</summary>
    public static bool SsidValid(string s) =>
        s.Length is >= 1 and <= 32 && !s.Any(c => char.IsControl(c) || c == ',');
}
