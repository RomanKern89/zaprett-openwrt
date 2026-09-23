using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;

namespace Zaprett.Core.Presets;

/// <summary>An item set of a preset service: the core one (Id null) or a variant (router contract v1.5 §16.3).</summary>
public sealed record ServiceSet(
    string? Id, IReadOnlyList<string> Lists, IReadOnlyList<string> Ipsets, IReadOnlyList<string> Sources, string? Tier,
    JsonNode? Name = null, JsonNode? NameEn = null, JsonNode? Description = null, JsonNode? DescriptionEn = null)
{
    public int Size => Lists.Count + Ipsets.Count + Sources.Count;
}

/// <summary>A check target of a preset service.</summary>
public sealed record Target(string Url, long MinBytes, string? Service);

/// <summary>A preset service with its valid targets (tester.preset_services).</summary>
public sealed record ServiceTargets(string Id, string Name, IReadOnlyList<Target> Targets);

/// <summary>Pure preset logic (port of the preset parts of commands.uc and tester.uc).</summary>
public static class PresetLogic
{
    public const int TierFullMinRamMib = 200;

    public static JsonObject? Load(string presetsFile)
    {
        var p = Files.ReadJson(presetsFile, 1048576);
        return p?["services"] is JsonArray ? p : null;
    }

    public static IEnumerable<JsonObject> Services(JsonObject? presets) =>
        (presets?["services"] as JsonArray ?? []).OfType<JsonObject>().Where(s => R.Str(s["id"]) != null);

    static List<string> StrList(JsonNode? n) => R.Strings(n);

    /// <summary>"&lt;service&gt;" or "&lt;service&gt;:&lt;variant&gt;" (router contract v1.7 §16.4); null when a part is not an id.</summary>
    public static (string Id, string? Variant)? ParseServiceRef(string? r)
    {
        if (r == null)
            return null;
        var parts = r.Split(':');
        if (parts.Length == 1 && Validate.IsId(parts[0]))
            return (parts[0], null);
        if (parts.Length == 2 && Validate.IsId(parts[0]) && Validate.IsId(parts[1]))
            return (parts[0], parts[1]);
        return null;
    }

    /// <summary>The core set first, then the variants; a variant without a tier inherits the service tier; a bad or
    /// repeated variant id is ignored.</summary>
    public static List<ServiceSet> Sets(JsonObject s)
    {
        var o = new List<ServiceSet> { new(null, StrList(s["lists"]), StrList(s["ipsets"]), StrList(s["sources"]), R.Str(s["tier"])) };
        foreach (var v in (s["variants"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = R.Str(v["id"]);
            if (!Validate.IsId(id) || o.Any(x => x.Id == id))
                continue;
            o.Add(new ServiceSet(id, StrList(v["lists"]), StrList(v["ipsets"]), StrList(v["sources"]), R.Str(v["tier"]) ?? R.Str(s["tier"]),
                v["name"]?.DeepClone(), v["name_en"]?.DeepClone(), v["description"]?.DeepClone(), v["description_en"]?.DeepClone()));
        }
        return o;
    }

    public static string SourceItemId(string name) => "src-" + name;

    /// <summary>Items of a set that are switched on; a subscription counts when it is enabled and its item is active.</summary>
    public static int SetActive(ServiceSet set, ZaprettConfig cfg, IReadOnlyDictionary<string, SourceConfig> srcs)
    {
        var n = set.Lists.Count(cfg.Lists.Contains) + set.Ipsets.Count(cfg.Ipsets.Contains);
        foreach (var name in set.Sources)
            if (srcs.TryGetValue(name, out var src) && src.Enabled &&
                Store.ItemTypes.OptionOf(src.Type) is { } opt && cfg.ListOption(opt).Contains(SourceItemId(name)))
                n++;
        return n;
    }

    /// <summary>Variant whose items are all on (largest wins, first on a tie), or null.</summary>
    public static string? EnabledVariant(JsonObject s, ZaprettConfig cfg, IReadOnlyDictionary<string, SourceConfig> srcs)
    {
        string? best = null;
        var bestSize = 0;
        foreach (var set in Sets(s).Skip(1))
            if (set.Size > 0 && SetActive(set, cfg, srcs) == set.Size && set.Size > bestSize)
            {
                best = set.Id;
                bestSize = set.Size;
            }
        return best;
    }

    /// <summary>A service is switched on when one of its lists, ipsets or subscription items (or a variant's) is active.</summary>
    public static bool ServiceActive(JsonObject s, ZaprettConfig cfg)
    {
        if (StrList(s["lists"]).Any(cfg.Lists.Contains) || StrList(s["ipsets"]).Any(cfg.Ipsets.Contains))
            return true;
        if (StrList(s["sources"]).Any(n => cfg.Lists.Contains("src-" + n) || cfg.Ipsets.Contains("src-" + n)))
            return true;
        return (s["variants"] as JsonArray ?? []).OfType<JsonObject>().Any(v => ServiceActive(new JsonObject
        {
            ["lists"] = v["lists"]?.DeepClone(), ["ipsets"] = v["ipsets"]?.DeepClone(), ["sources"] = v["sources"]?.DeepClone(),
        }, cfg));
    }

    public static List<Target> ServiceTargetList(JsonObject s)
    {
        var o = new List<Target>();
        foreach (var t in (s["test_targets"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var url = R.Str(t["url"]);
            if (!Validate.UrlValid(url))
                continue;
            var mb = t["min_bytes"] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? Validate.ParseUint(v.GetValue<string>(), 0, 1073741824)
                : R.Long(t["min_bytes"]) is { } l && l is >= 0 and <= 1073741824 ? (int)l : null;
            o.Add(new Target(url!, mb ?? 0, R.Str(s["id"])));
        }
        return o;
    }

    /// <summary>Services to check: ids null → the switched-on ones; otherwise the named ones (unknown id → error id).</summary>
    public static (List<ServiceTargets>? Services, string? UnknownId) PresetServices(JsonObject? presets, ZaprettConfig cfg, IReadOnlyList<string>? ids)
    {
        var all = Services(presets).ToList();
        var o = new List<ServiceTargets>();
        if (ids != null)
        {
            foreach (var id in ids)
            {
                var s = all.FirstOrDefault(x => R.Str(x["id"]) == id);
                if (s == null)
                    return (null, id);
                o.Add(new ServiceTargets(id, R.Str(s["name"]) ?? id, ServiceTargetList(s)));
            }
            return (o, null);
        }
        foreach (var s in all)
            if (ServiceActive(s, cfg))
                o.Add(new ServiceTargets(R.Str(s["id"])!, R.Str(s["name"]) ?? R.Str(s["id"])!, ServiceTargetList(s)));
        return (o, null);
    }

    /// <summary>Targets of the switched-on services + up to maxDomains domains of the active lists (https://d/, min 0).</summary>
    public static List<Target> BuildTargets(JsonObject? presets, ZaprettConfig cfg, IEnumerable<string> listTexts, int maxDomains)
    {
        var targets = new List<Target>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in PresetServices(presets, cfg, null).Services!)
            foreach (var t in s.Targets)
                if (seen.Add(t.Url))
                    targets.Add(t);
        var n = 0;
        foreach (var text in listTexts)
        {
            foreach (var raw in (text ?? "").Split('\n'))
            {
                if (n >= maxDomains)
                    break;
                var l = raw.Trim().ToLowerInvariant();
                if (l.Length == 0 || l[0] is '#' or ';' or '/')
                    continue;
                if (l[0] == '^')
                    l = l[1..];
                if (!Validate.DomainValid(l) || Validate.Ipv4Valid(l) || Validate.Ipv6Valid(l) || !l.Contains('.', StringComparison.Ordinal) ||
                    l.Contains('_', StringComparison.Ordinal))
                    continue;
                var url = "https://" + l + "/";
                if (!seen.Add(url))
                    continue;
                targets.Add(new Target(url, 0, null));
                n++;
            }
        }
        return targets;
    }

    /// <summary>Too heavy for this PC (router warning low_memory): a switched-on "full" service or variant with less RAM
    /// than tiers.full.min_ram_mib, or enabled subscriptions needing more than half of the available memory.</summary>
    public static bool MemoryHeavy(ZaprettConfig cfg, JsonObject? presets, IReadOnlyList<SourceConfig> sources, long? ramTotal, long? memAvailable)
    {
        var minFull = R.Long(presets?["tiers"]?["full"]?["min_ram_mib"]) ?? TierFullMinRamMib;
        var srcs = sources.ToDictionary(s => s.Name);
        if (ramTotal != null && ramTotal < minFull)
            foreach (var s in Services(presets))
            {
                if (R.Str(s["tier"]) == "full" && ServiceActive(new JsonObject
                    {
                        ["lists"] = s["lists"]?.DeepClone(), ["ipsets"] = s["ipsets"]?.DeepClone(), ["sources"] = s["sources"]?.DeepClone(),
                    }, cfg))
                    return true;
                foreach (var set in Sets(s).Skip(1))
                    if (set.Tier == "full" && set.Size > 0 && SetActive(set, cfg, srcs) == set.Size)
                        return true;
            }
        var need = sources.Where(s => s.Enabled && s.Valid).Sum(s => (long)s.RamMib);
        return memAvailable != null && need * 2 > memAvailable;
    }

    /// <summary>A switched-on service that needs encrypted DNS while the DNS is plain (warning dns_plain).</summary>
    public static bool DnsPlain(ZaprettConfig cfg, JsonObject? presets, bool encrypted) =>
        !encrypted && Services(presets).Any(s => R.Bool(s["needs_dns"]) && ServiceActive(s, cfg));

    public static string? DefaultStrategy(JsonObject? presets, string engine)
    {
        var d = R.Str(presets?["defaults"]?[engine == Engines.Winws2 ? "strategy_nfqws2" : "strategy"]);
        return Validate.IsId(d) ? d : null;
    }
}
