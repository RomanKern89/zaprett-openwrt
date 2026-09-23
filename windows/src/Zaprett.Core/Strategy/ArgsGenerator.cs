using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zaprett.Core.Config;
using Zaprett.Core.Guard;
using Zaprett.Core.Platform;
using Zaprett.Core.Store;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;
using Zaprett.Core.Text;

namespace Zaprett.Core.Strategy;

/// <summary>An active list with its file and entry count (null = unknown, counts as non-empty).</summary>
public sealed record ListRef(string Id, string File, int? Entries, string Source);

public sealed record GenerateOptions
{
    public StoreIndex? Index { get; init; }
    /// <summary>Engine (winws, winws2); default — the configured one.</summary>
    public string? Engine { get; init; }
    public string? Strategy { get; init; }
    /// <summary>Strategy text instead of an installed strategy (strategy.save); <see cref="Item"/> describes it.</summary>
    public string? Text { get; init; }
    public StoreItem? Item { get; init; }
    public bool TestMode { get; init; }
    /// <summary>NLM networks (names or GUIDs) for --nlm-filter when network_filter.skip_corporate is on: the networks
    /// that are not domain-authenticated, supplied by the service (<see cref="INetworkList"/>).</summary>
    public IReadOnlyList<string>? NlmNetworks { get; init; }
}

/// <summary>Result of <see cref="ArgsGenerator.Build"/>: argv or an error in the router format.</summary>
public sealed class GenerateResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public JsonObject Extra { get; init; } = [];
    public string Engine { get; init; } = Engines.Winws;
    public string StrategyId { get; init; } = "";
    public string StrategyName { get; init; } = "";
    public string StrategySource { get; init; } = "";
    /// <summary>Full argv of the engine (base options first).</summary>
    public List<string> Args { get; init; } = [];
    /// <summary>The strategy part of argv (what the router conformance compares).</summary>
    public List<string> StrategyArgs { get; init; } = [];
    public List<string> TcpPorts { get; init; } = [];
    public List<string> UdpPorts { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public JsonObject Details { get; init; } = [];
    public List<string> Dropped { get; init; } = [];
    public bool TestMode { get; init; }

    public static GenerateResult Fail(string code, string message, JsonObject? extra = null) =>
        new() { Ok = false, Error = code, Message = message, Extra = extra ?? [] };

    public JsonObject StrategyJson() =>
        new() { ["id"] = StrategyId, ["name"] = StrategyName, ["source"] = StrategySource };

    public JsonObject PortsJson() => new() { ["tcp"] = R.Arr(TcpPorts), ["udp"] = R.Arr(UdpPorts) };

    /// <summary>{ok:false,error,message,...} for a failed build.</summary>
    public JsonObject ToFail() => R.Fail(Error ?? "generate_failed", Message ?? "", Extra);
}

/// <summary>Engine argument generator (ARCHITECTURE-WIN §4): the algorithm of the router generator (strategy.uc,
/// router contract §5) with the Windows interception options instead of the router ones.</summary>
public sealed partial class ArgsGenerator
{
    public static readonly IReadOnlyDictionary<string, string> LegacyModes = new Dictionary<string, string>
    {
        ["split"] = "fakedsplit", ["split2"] = "multisplit", ["disorder"] = "fakeddisorder", ["disorder2"] = "multidisorder",
    };

    /// <summary>Options owned by zaprett: removed from the strategy text with warning strategy_option_ignored. The router
    /// list plus the WinDivert interception and network filters of winws.</summary>
    public static readonly IReadOnlyList<string> ReservedOptions =
    [
        "qnum", "user", "uid", "daemon", "pidfile", "debug", "dry-run", "version", "dpi-desync-fwmark", "fwmark", "intercept",
        "chdir", "writable",
        "wf-iface", "wf-l3", "wf-tcp", "wf-udp", "wf-raw", "wf-raw-part", "wf-filter-lan", "wf-save", "ssid-filter",
        "nlm-filter", "nlm-list",
        // winws2 (zapret2): interception by direction and its other WinDivert and network filters
        "wf-tcp-in", "wf-tcp-out", "wf-udp-in", "wf-udp-out", "wf-tcp-empty", "wf-icmp-in", "wf-icmp-out", "wf-ipp-in", "wf-ipp-out",
        "wf-raw-filter", "wf-filter-loopback", "wf-dup-check", "ssid-filter-neg", "nlm-filter-neg",
    ];

    public static readonly IReadOnlyDictionary<string, string> PlaceholderTypes = new Dictionary<string, string>
    {
        ["hostlist"] = "list", ["hostlist_exclude"] = "list_exclude", ["ipset"] = "ipset", ["ipset_exclude"] = "ipset_exclude",
        ["bin"] = "bin", ["lua_lib"] = "lua_lib",
    };

    /// <summary>Options whose value is a file the engine reads.</summary>
    public static readonly IReadOnlyList<string> FileOptions =
    [
        "hostlist", "hostlist-exclude", "hostlist-auto", "hostlist-auto-debug", "ipset", "ipset-exclude",
        "dpi-desync-fake-http", "dpi-desync-fake-tls", "dpi-desync-fake-unknown", "dpi-desync-fake-syndata", "dpi-desync-fake-quic",
        "dpi-desync-fake-wireguard", "dpi-desync-fake-dht", "dpi-desync-fake-discord", "dpi-desync-fake-stun",
        "dpi-desync-fake-unknown-udp", "dpi-desync-split-seqovl-pattern", "dpi-desync-fakedsplit-pattern",
        "dpi-desync-udplen-pattern", "blob", "lua-init",
    ];

    public static readonly IReadOnlyList<string> AutohostlistOptions = ["hostlist-auto", "hostlist-auto-debug"];

    public static readonly IReadOnlyList<string> IncludeFilters = ["hostlist", "hostlist-domains", "hostlist-auto", "ipset", "ipset-ip"];

    public static readonly IReadOnlyList<PortRange> DefaultTcpPorts = [new(80, 80), new(443, 443)];
    public static readonly IReadOnlyList<PortRange> DefaultUdpPorts = [new(443, 443)];
    public const int WideRange = 1024;

    public const string GameBinTcp = "tls_clienthello_4pda_to";
    public const string GameBinUdp = "quic_initial_www_google_com";

    /// <summary>Lua files of zapret2 loaded when a strategy has no --lua-init of its own (zapret-auto holds circular).</summary>
    public static readonly IReadOnlyList<string> BaseLua = ["zapret-lib.lua", "zapret-antidpi.lua", "zapret-auto.lua"];

    [GeneratedRegex("^--?([A-Za-z0-9-]+)(=.*)?$", RegexOptions.Singleline)]
    private static partial Regex OptionName();

    [GeneratedRegex("^--?([A-Za-z0-9-]+)=(.*)$", RegexOptions.Singleline)]
    private static partial Regex OptionValue();

    [GeneratedRegex("^--?filter-(tcp|udp)=(.*)$", RegexOptions.Singleline)]
    private static partial Regex PortOption();

    [GeneratedRegex("\\$\\{([^}]*)\\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^(hostlist|hostlist_exclude|ipset|ipset_exclude|bin|lua_lib):(.*)$", RegexOptions.Singleline)]
    private static partial Regex TypedPlaceholder();

    [GeneratedRegex("^0[xX][0-9a-fA-F]*$")]
    private static partial Regex HexValue();

    [GeneratedRegex("^(\\+[0-9]+)?@(.*)$", RegexOptions.Singleline)]
    private static partial Regex AtFile();

    [GeneratedRegex("^--?hostlist-auto(-debug)?=")]
    private static partial Regex AutoHostlistArg();

    readonly IPaths paths;
    readonly ItemStore store;

    public ArgsGenerator(IPaths paths, ItemStore store)
    {
        this.paths = paths;
        this.store = store;
    }

    public string ZaprettDir => Path.Combine(paths.BundleDir, "files");

    public string AutohostlistDir => Path.Combine(paths.RunDir, "autohostlist");

    public string DebugLog => Path.Combine(paths.RunDir, "engine-debug.log");

    public string LuaDir => Path.Combine(paths.Engine2Dir, "lua");

    /// <summary>Roots every file of a strategy must lie in: ProgramData\zaprett (installed, user, run, guard), the bundle
    /// and the Lua directory of zapret2.</summary>
    public IReadOnlyList<string> AllowedRoots => [paths.DataDir, paths.BundleDir, LuaDir];

    /// <summary>Directories --lua-init may load from: the Lua directory of zapret2 and the lua items (bundle, installed).</summary>
    public IReadOnlyList<string> LuaRoots =>
        [LuaDir, Path.Combine(paths.BundleDir, "files", "lua"), Path.Combine(paths.InstalledDir, "files", "lua")];

    /* ---------- pure steps (router contract §5) ---------- */

    /// <summary>Step 1: tokens like split_whitespace(), without trailing '\' and "--comment" junk.</summary>
    public static (List<string> Tokens, List<string> Dropped) Tokenize(string? text)
    {
        var tokens = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Replace('\t', ' ').Replace('\r', ' ').Replace('\v', ' ').Replace('\f', ' ').TrimEnd(' ');
            while (line.EndsWith('\\'))
                line = line[..^1].TrimEnd(' ');
            foreach (var t in line.Split(' '))
                if (t.Length > 0 && t != "\\")
                    tokens.Add(t);
        }
        var o = new List<string>();
        var dropped = new List<string>();
        var skipping = false;
        foreach (var t in tokens)
        {
            if (skipping)
            {
                if (t.StartsWith("--", StringComparison.Ordinal))
                {
                    skipping = false;
                }
                else
                {
                    dropped.Add(t);
                    continue;
                }
            }
            if (t == "--comment")
            {
                skipping = true;
                dropped.Add(t);
                continue;
            }
            o.Add(t);
        }
        return (o, dropped);
    }

    /// <summary>Step 2: legacy --dpi-desync modes (winws only).</summary>
    public static List<string> NormalizeModes(IEnumerable<string> tokens) =>
        tokens.Select(t => !t.StartsWith("--dpi-desync=", StringComparison.Ordinal) ? t :
            "--dpi-desync=" + string.Join(',', t[13..].Split(',').Select(m => LegacyModes.TryGetValue(m, out var n) ? n : m))).ToList();

    /// <summary>Removes the options zaprett owns and, with an engine given, the options only the router engine knows.</summary>
    public static (List<string> Tokens, List<string> Ignored) StripReserved(IEnumerable<string> tokens, string? engine = null)
    {
        var o = new List<string>();
        var ignored = new List<string>();
        foreach (var t in tokens)
        {
            var m = OptionName().Match(t);
            if (m.Success && (ReservedOptions.Contains(m.Groups[1].Value) ||
                (engine != null && EngineOptions.IsLinuxOnly(engine, m.Groups[1].Value))))
            {
                if (!ignored.Contains(m.Groups[1].Value))
                    ignored.Add(m.Groups[1].Value);
                continue;
            }
            o.Add(t);
        }
        return (o, ignored);
    }

    public static List<List<string>> SplitProfiles(IEnumerable<string> tokens)
    {
        var profiles = new List<List<string>> { new() };
        foreach (var t in tokens)
        {
            if (t == "--new")
                profiles.Add([]);
            else if (IsNamedNew(t))
                profiles.Add([t]);
            else
                profiles[^1].Add(t);
        }
        return profiles;
    }

    public static List<string> JoinProfiles(IEnumerable<IReadOnlyList<string>> profiles)
    {
        var o = new List<string>();
        foreach (var p in profiles)
        {
            if (o.Count > 0 && !(p.Count > 0 && IsNamedNew(p[0])))
                o.Add("--new");
            o.AddRange(p);
        }
        return o;
    }

    /// <summary>winws2 also starts a profile with --new=&lt;name&gt; (router strategy.uc is_named_new): the token stays the
    /// first one of its profile.</summary>
    static bool IsNamedNew(string t) => t.StartsWith("--new=", StringComparison.Ordinal);

    static bool ListNonEmpty(IEnumerable<ListRef> lists) => lists.Any(l => l.Entries == null || l.Entries > 0);

    sealed record Env(
        string Mode, List<ListRef> Lists, List<ListRef> ExcludeLists, List<ListRef> Ipsets, List<ListRef> ExcludeIpsets,
        string GuardHostlist, string GuardIpset, string ZaprettDir, IReadOnlyList<string>? DeclaredDeps,
        Func<string, string, StoreItem?> Resolve);

    static (string? Token, GenerateResult? Error) ExpandInline(string t, Env env)
    {
        GenerateResult? err = null;
        var res = Placeholder().Replace(t, m =>
        {
            if (err != null)
                return m.Value;
            var inner = m.Groups[1].Value;
            if (inner is "hostlists" or "ipsets")
            {
                err = GenerateResult.Fail("placeholder_in_token",
                    T.S("gen.placeholder_in_token", "${" + inner + "}", t));
                return m.Value;
            }
            if (inner == "zaprettdir")
                return env.ZaprettDir;
            var tm = TypedPlaceholder().Match(inner);
            if (!tm.Success)
            {
                err = GenerateResult.Fail("unknown_placeholder", T.S("gen.unknown_placeholder", "${" + inner + "}"));
                return m.Value;
            }
            var id = tm.Groups[2].Value;
            if (!Validate.IsId(id))
            {
                err = GenerateResult.Fail("bad_placeholder_id", T.S("gen.bad_placeholder_id", "${" + inner + "}"));
                return m.Value;
            }
            if (env.DeclaredDeps is { Count: > 0 } deps && !deps.Contains(id))
            {
                err = GenerateResult.Fail("dependency_not_declared", T.S("gen.dependency_not_declared", id));
                return m.Value;
            }
            var r = env.Resolve(PlaceholderTypes[tm.Groups[1].Value], id);
            if (r == null)
            {
                err = GenerateResult.Fail("item_not_installed",
                    T.S("gen.item_not_installed", id, tm.Groups[1].Value), new JsonObject { ["item"] = id });
                return m.Value;
            }
            return r.File;
        });
        if (err != null)
            return (null, err);
        if (res.Contains("${", StringComparison.Ordinal))
            return (null, GenerateResult.Fail("unknown_placeholder", T.S("gen.unclosed_placeholder", t)));
        // ${zaprettdir}/bin/x.bin: one separator style inside the path
        var zi = res.IndexOf(env.ZaprettDir, StringComparison.Ordinal);
        if (zi >= 0 && t.Contains("${zaprettdir}", StringComparison.Ordinal))
            res = res[..zi] + res[zi..].Replace('/', '\\');
        return (res, null);
    }

    static (List<string>? Tokens, GenerateResult? Error) ExpandProfile(List<string> tokens, Env env)
    {
        var wl = env.Mode != "blacklist";
        var hasH = tokens.Contains("${hostlists}");
        var hasI = tokens.Contains("${ipsets}");
        bool emitH = true, emitI = true;
        // the engine ANDs ipset and hostlist filters of one profile and an empty list filter passes everything: in a
        // profile with both placeholders only the kind with real entries is used (router strategy.uc)
        if (wl && hasH && hasI)
        {
            bool hn = ListNonEmpty(env.Lists), inn = ListNonEmpty(env.Ipsets);
            if (hn && !inn)
                emitI = false;
            else if (!hn && inn)
                emitH = false;
        }
        var o = new List<string>();
        foreach (var t in tokens)
        {
            if (t == "${hostlists}")
            {
                if (!emitH)
                    continue;
                if (wl)
                {
                    o.AddRange(env.Lists.Select(l => "--hostlist=" + l.File));
                    o.Add("--hostlist=" + env.GuardHostlist);
                }
                o.AddRange(env.ExcludeLists.Select(l => "--hostlist-exclude=" + l.File));
                // contract v1.2 §5: a whitelist profile with only ${hostlists} also gets the IP exclusions
                if (wl && !hasI)
                    o.AddRange(env.ExcludeIpsets.Select(l => "--ipset-exclude=" + l.File));
                continue;
            }
            if (t == "${ipsets}")
            {
                if (wl && emitI)
                {
                    o.AddRange(env.Ipsets.Select(l => "--ipset=" + l.File));
                    o.Add("--ipset=" + env.GuardIpset);
                }
                o.AddRange(env.ExcludeIpsets.Select(l => "--ipset-exclude=" + l.File));
                continue;
            }
            if (!t.Contains("${", StringComparison.Ordinal))
            {
                o.Add(t);
                continue;
            }
            var (tok, err) = ExpandInline(t, env);
            if (err != null)
                return (null, err);
            o.Add(tok!);
        }
        return (o, null);
    }

    /// <summary>Game filter (router contract v1.4 §15.2): profiles for the game ports, matched only by the active
    /// include ipsets; null profiles when there is none (warning game_filter_no_ipsets).</summary>
    static (List<List<string>>? Profiles, GenerateResult? Error) GameProfiles(ZaprettConfig cfg, string engine, Env env)
    {
        if (!ListNonEmpty(env.Ipsets))
            return (null, null);
        var filt = env.Ipsets.Select(l => "--ipset=" + l.File).ToList();
        filt.Add("--ipset=" + env.GuardIpset);
        filt.AddRange(env.ExcludeIpsets.Select(l => "--ipset-exclude=" + l.File));
        var o = new List<List<string>>();
        foreach (var proto in new[] { "tcp", "udp" })
        {
            var ports = proto == "tcp" ? cfg.GamePortsTcp : cfg.GamePortsUdp;
            if (string.IsNullOrEmpty(ports))
                continue;
            var id = proto == "tcp" ? GameBinTcp : GameBinUdp;
            var f = env.Resolve("bin", id)?.File;
            if (f == null)
                return (null, GenerateResult.Fail("item_not_installed",
                    T.S("gen.game_item_not_installed", id), new JsonObject { ["item"] = id }));
            var p = new List<string> { $"--filter-{proto}={ports}" };
            p.AddRange(filt);
            if (engine == Engines.Winws2)
            {
                if (proto == "tcp")
                    p.AddRange(["--blob=zaprett_game_tcp:@" + f, "--out-range=<n3", "--payload=all",
                        "--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=zaprett_game_tcp"]);
                else
                    p.AddRange(["--blob=zaprett_game_udp:@" + f, "--out-range=<n2", "--payload=all",
                        "--lua-desync=fake:blob=zaprett_game_udp:repeats=12"]);
            }
            else if (proto == "tcp")
            {
                p.AddRange(["--dpi-desync=multisplit", "--dpi-desync-any-protocol=1", "--dpi-desync-cutoff=n3",
                    "--dpi-desync-split-seqovl=568", "--dpi-desync-split-pos=1", "--dpi-desync-split-seqovl-pattern=" + f]);
            }
            else
            {
                p.AddRange(["--dpi-desync=fake", "--dpi-desync-repeats=12", "--dpi-desync-any-protocol=1",
                    "--dpi-desync-fake-unknown-udp=" + f, "--dpi-desync-cutoff=n2"]);
            }
            o.Add(p);
        }
        return (o, null);
    }

    /// <summary>Step 6: ports from --filter-tcp/--filter-udp of every profile. Returns null on a bad filter.</summary>
    public static (List<PortRange> Tcp, List<PortRange> Udp)? ExtractPorts(IEnumerable<string> tokens)
    {
        var tcp = new List<PortRange>();
        var udp = new List<PortRange>();
        foreach (var prof in SplitProfiles(tokens))
        {
            if (prof.Contains("--skip"))
                continue;
            bool hasT = false, hasU = false, negT = false, negU = false;
            var rT = new List<PortRange>();
            var rU = new List<PortRange>();
            foreach (var t in prof)
            {
                var m = PortOption().Match(t);
                if (!m.Success)
                    continue;
                var p = Validate.ParsePortFilter(m.Groups[2].Value);
                if (p == null)
                    return null;
                var isTcp = m.Groups[1].Value == "tcp";
                if (isTcp)
                {
                    hasT = true;
                    negT |= p.Negated;
                    if (!p.Negated)
                        rT.AddRange(p.Ranges);
                }
                else
                {
                    hasU = true;
                    negU |= p.Negated;
                    if (!p.Negated)
                        rU.AddRange(p.Ranges);
                }
            }
            if (!hasT && !hasU)
            {
                tcp.AddRange(DefaultTcpPorts);
                udp.AddRange(DefaultUdpPorts);
                continue;
            }
            if (hasT)
                tcp.AddRange(negT ? DefaultTcpPorts : rT);
            if (hasU)
                udp.AddRange(negU ? DefaultUdpPorts : rU);
        }
        return (Validate.MergeRanges(tcp), Validate.MergeRanges(udp));
    }

    /// <summary>Profiles (1-based) without a hostlist/ipset include filter, acting on all traffic of their ports.</summary>
    public static JsonArray UnfilteredProfiles(IEnumerable<string> tokens)
    {
        var res = new JsonArray();
        var n = 0;
        foreach (var prof in SplitProfiles(tokens))
        {
            n++;
            if (prof.Contains("--skip"))
                continue;
            var filtered = prof.Any(t => OptionValue().Match(t) is { Success: true } m && IncludeFilters.Contains(m.Groups[1].Value));
            if (filtered)
                continue;
            var p = ExtractPorts(prof);
            res.Add(new JsonObject
            {
                ["profile"] = n,
                ["tcp"] = R.Arr(p == null ? [] : Validate.RangesToStrings(p.Value.Tcp)),
                ["udp"] = R.Arr(p == null ? [] : Validate.RangesToStrings(p.Value.Udp)),
            });
        }
        return res;
    }

    static bool PathAllowed(string path, IEnumerable<string> roots)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return false;
        foreach (var seg in path.Split('\\', '/'))
            if (seg is "." or "..")
                return false;
        return roots.Any(r => Files.IsUnder(path, r));
    }

    /// <summary>Every file named by a strategy must lie in zaprett's own directories, so a custom strategy cannot make the
    /// engine read (and send out as a fake packet) arbitrary files; --lua-init only from the Lua directories.</summary>
    public GenerateResult? CheckFileOptions(IEnumerable<string> tokens)
    {
        foreach (var t in tokens)
        {
            var m = OptionValue().Match(t);
            if (!m.Success || !FileOptions.Contains(m.Groups[1].Value))
                continue;
            var opt = m.Groups[1].Value;
            var v = m.Groups[2].Value;
            if (AutohostlistOptions.Contains(opt))
            {
                if (!PathAllowed(v, [AutohostlistDir]))
                    return GenerateResult.Fail("path_not_allowed", T.S("gen.autohostlist_dir", opt, AutohostlistDir + "\\"));
                continue;
            }
            if (opt == "blob")
            {
                var c = v.IndexOf(':', StringComparison.Ordinal);
                if (c < 0)
                    return GenerateResult.Fail("bad_option", T.S("gen.bad_value", t));
                v = v[(c + 1)..];
            }
            if (HexValue().IsMatch(v) || v == "!")
                continue;
            if (opt == "lua-init" && !v.StartsWith('@'))
                return GenerateResult.Fail("path_not_allowed", T.S("gen.inline_lua"));
            var fm = AtFile().Match(v);
            if (fm.Success)
                v = fm.Groups[2].Value;
            if (opt == "lua-init" && !PathAllowed(v, LuaRoots))
                return GenerateResult.Fail("path_not_allowed", T.S("gen.lua_dir", v));
            if (!PathAllowed(v, AllowedRoots))
                return GenerateResult.Fail("path_not_allowed", T.S("gen.path_outside", v, opt));
        }
        return null;
    }

    /// <summary>Base options of winws/winws2 (ARCHITECTURE-WIN §4): debug log, WinDivert filter by the ports of the
    /// strategy, network filters, and for winws2 the Lua libraries when the strategy has no --lua-init of its own.</summary>
    public List<string> BaseOptions(ZaprettConfig cfg, string engine, IReadOnlyList<string> strategyTokens,
        IReadOnlyList<string> tcp, IReadOnlyList<string> udp, IReadOnlyList<string>? nlm)
    {
        var b = new List<string>();
        if (cfg.Debug)
            b.Add("--debug=@" + DebugLog);
        b.Add(cfg.Ipv6 ? "--wf-l3=ipv4,ipv6" : "--wf-l3=ipv4");
        // winws2 has no --wf-tcp/--wf-udp (zapret2 nfq2/nfqws.c): outgoing ports, incoming SYN are intercepted by itself
        var two = engine == Engines.Winws2;
        if (tcp.Count > 0)
            b.Add((two ? "--wf-tcp-out=" : "--wf-tcp=") + string.Join(',', tcp));
        if (udp.Count > 0)
            b.Add((two ? "--wf-udp-out=" : "--wf-udp=") + string.Join(',', udp));
        if (cfg.NetworkFilter.Mode == "ssids" && cfg.NetworkFilter.Ssids.Count > 0)
            b.Add("--ssid-filter=" + string.Join(',', cfg.NetworkFilter.Ssids));
        if (cfg.NetworkFilter.SkipCorporate && nlm is { Count: > 0 })
            b.Add("--nlm-filter=" + string.Join(',', nlm));
        if (engine == Engines.Winws2 && !strategyTokens.Any(t => t.StartsWith("--lua-init=", StringComparison.Ordinal)))
            b.AddRange(BaseLua.Select(f => "--lua-init=@" + Path.Combine(LuaDir, f)));
        return b;
    }

    List<ListRef> ResolveLists(StoreIndex idx, IEnumerable<string> ids, string type, List<string> missing, List<string> notDownloaded)
    {
        var res = new List<ListRef>();
        foreach (var id in ids)
        {
            var it = idx.Get(type, id);
            if (it == null)
            {
                (id.StartsWith("src-", StringComparison.Ordinal) ? notDownloaded : missing).Add(id);
                continue;
            }
            res.Add(new ListRef(id, it.File, store.EntriesOf(it), it.Source));
        }
        return res;
    }

    /// <summary>Full pipeline without running the engine (router strategy.build).</summary>
    public GenerateResult Build(ZaprettConfig cfg, GenerateOptions? opts = null)
    {
        opts ??= new GenerateOptions();
        var idx = opts.Index ?? store.Scan();
        var engine = opts.Engine ?? cfg.Engine;
        var itype = Engines.ItemType(engine);
        var sid = opts.Strategy ?? cfg.CurrentStrategy(engine);
        var warnings = new List<string>();
        var details = new JsonObject();
        var item = opts.Item;
        var text = opts.Text;

        if (text == null)
        {
            if (string.IsNullOrEmpty(sid))
                return GenerateResult.Fail("no_strategy", engine == Engines.Winws2 ? T.S("strategy.no_strategy_winws2") : T.S("strategy.no_strategy"));
            item = idx.Get(itype, sid);
            if (item == null)
                return GenerateResult.Fail("strategy_not_found", T.S("strategy.not_found_engine", sid, engine),
                    new JsonObject { ["strategy"] = sid });
            text = ItemStore.ReadStrategyText(item);
            if (text == null)
                return GenerateResult.Fail("strategy_unreadable",
                    T.S("strategy.unreadable_id", sid));
        }
        item ??= new StoreItem(sid, itype, sid, "", "", "", null, null, [], "", "user", null, null, null, null, null);

        var (tokens, dropped) = Tokenize(text);
        // full option names first: the engine accepts abbreviations, the checks below compare full names
        var (canon, cerr) = EngineOptions.Canonicalize(tokens, engine);
        if (cerr != null)
            return GenerateResult.Fail("bad_option", cerr);
        tokens = canon!;
        if (engine == Engines.Winws)
            tokens = NormalizeModes(tokens);
        var (stripped, ignored) = StripReserved(tokens, engine);
        tokens = stripped;
        if (ignored.Count > 0)
        {
            warnings.Add("strategy_option_ignored");
            details["ignored_options"] = R.Arr(ignored);
        }

        var missing = new List<string>();
        var notDownloaded = new List<string>();
        var env = new Env(
            cfg.ListMode,
            ResolveLists(idx, cfg.Lists, "list", missing, notDownloaded),
            ResolveLists(idx, cfg.ExcludeLists, "list_exclude", missing, notDownloaded),
            ResolveLists(idx, cfg.Ipsets, "ipset", missing, notDownloaded),
            ResolveLists(idx, cfg.ExcludeIpsets, "ipset_exclude", missing, notDownloaded),
            GuardFiles.Hostlist(paths), GuardFiles.Ipset(paths), ZaprettDir,
            item.Source == "user" ? null : item.Dependencies,
            idx.Get);
        if (missing.Count > 0)
            return GenerateResult.Fail("item_not_found",
                T.S("gen.lists_not_found", string.Join(", ", missing)),
                new JsonObject { ["missing_lists"] = R.Arr(missing) });
        if (notDownloaded.Count > 0)
        {
            warnings.Add("source_not_downloaded");
            details["sources_not_downloaded"] = R.Arr(notDownloaded);
        }

        var profiles = new List<IReadOnlyList<string>>();
        var emptyProfiles = 0;
        var usesHostlists = false;
        var split = SplitProfiles(tokens);
        foreach (var prof in split)
        {
            if (prof.Count == 0)
            {
                emptyProfiles++;
                continue;
            }
            if (prof.Contains("${hostlists}"))
                usesHostlists = true;
            var (exp, err) = ExpandProfile(prof, env);
            if (err != null)
                return err;
            profiles.Add(exp!);
        }
        if (profiles.Count == 0)
            return GenerateResult.Fail("strategy_empty", T.S("strategy.empty", item.Id));
        if (emptyProfiles > 0 && split.Count > 1)
        {
            warnings.Add("empty_profile_removed");
            details["empty_profiles"] = emptyProfiles;
        }
        if (usesHostlists && cfg.ListMode != "blacklist" && !ListNonEmpty(env.Lists))
            warnings.Add("no_active_lists");
        // the game filter goes to the end: the strategy's own profiles keep priority for their ports
        if (cfg.GameFilter)
        {
            var (gp, gerr) = GameProfiles(cfg, engine, env);
            if (gerr != null)
                return gerr;
            if (gp == null)
                warnings.Add("game_filter_no_ipsets");
            else
                profiles.AddRange(gp);
        }

        var strat = JoinProfiles(profiles);
        // a placeholder standing alone that expanded into a value is not an option either
        if (strat.FirstOrDefault(t => !t.StartsWith("--", StringComparison.Ordinal)) is { } word)
            return GenerateResult.Fail("bad_option", T.S("gen.not_an_option", word));
        if (CheckFileOptions(strat) is { } fc)
            return fc;
        var ports = ExtractPorts(strat);
        if (ports == null)
            return GenerateResult.Fail("bad_port_filter", T.S("gen.bad_port_filter"));
        var (tcpR, udpR) = ports.Value;
        if (tcpR.Concat(udpR).Any(r => r.Hi - r.Lo + 1 > WideRange))
            warnings.Add("wide_port_range");
        if (cfg.ListMode != "blacklist")
        {
            var uf = UnfilteredProfiles(strat);
            if (uf.Count > 0)
            {
                warnings.Add("profile_unfiltered");
                details["unfiltered_profiles"] = uf;
            }
        }
        var tcp = Validate.RangesToStrings(tcpR);
        var udp = Validate.RangesToStrings(udpR);
        var args = BaseOptions(cfg, engine, strat, tcp, udp, opts.NlmNetworks);
        args.AddRange(strat);
        return new GenerateResult
        {
            Ok = true, Engine = engine, StrategyId = item.Id, StrategyName = item.Name, StrategySource = item.Source,
            Args = args, StrategyArgs = strat, TcpPorts = tcp, UdpPorts = udp, Warnings = warnings, Details = details,
            Dropped = dropped, TestMode = opts.TestMode,
        };
    }

    /// <summary>Creates missing user list files the configuration names (the engine refuses absent files), the guard
    /// files, and the auto hostlist directory when the arguments use it.</summary>
    public void EnsureFiles(ZaprettConfig cfg, IReadOnlyList<string>? args = null)
    {
        GuardFiles.Ensure(paths);
        foreach (var opt in new[] { "lists", "exclude_lists", "ipsets", "exclude_ipsets" })
            foreach (var id in cfg.ListOption(opt))
            {
                var p = store.UserListPath(id);
                if (p.Length > 0 && !File.Exists(p))
                    Files.AtomicWriteText(p, "");
            }
        if (args != null && args.Any(a => AutoHostlistArg().IsMatch(a)))
            Directory.CreateDirectory(AutohostlistDir);
    }

    /// <summary>Step 5: the engine checks the arguments (winws --dry-run; winws2 --intercept=0) as the service.</summary>
    public async Task<(int Rc, string Output, bool Missing)> DryRunAsync(IProcessRunner runner, string engine,
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = Engines.Executable(paths, engine);
        if (!File.Exists(exe))
            return (-1, T.S("svc.engine_exe_missing", exe), true);
        var argv = new List<string> { engine == Engines.Winws2 ? "--intercept=0" : "--dry-run" };
        argv.AddRange(args.Where(a => !a.StartsWith("--debug", StringComparison.Ordinal)));
        // winws (zapret v72.13 nfqws.c) takes the mutex Global\winws_arg_<hash of the WinDivert filter options> BEFORE it
        // looks at --dry-run, so a check of the arguments of the running engine fails with "A copy of winws is already
        // running with the same filter". --wf-save exits (code 0) right before that mutex, after the same work: all
        // options parsed, lists and fakes loaded, the WinDivert filter built — so it checks the arguments without the
        // duplicate check. winws2 has no such problem: its duplicate check runs only with interception on.
        string? saved = null;
        if (engine == Engines.Winws)
        {
            Directory.CreateDirectory(paths.RunDir);
            saved = Path.Combine(paths.RunDir, "dryrun-filter-" + Guid.NewGuid().ToString("N") + ".txt");
            argv.Add("--wf-save=" + saved);
        }
        ProcessResult r;
        bool filterSaved;
        try
        {
            r = await runner.RunAsync(exe, argv, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            filterSaved = saved == null || (Files.Size(saved) ?? 0) > 0;
        }
        finally
        {
            if (saved != null)
                Files.TryDelete(saved);
        }
        var sb = new StringBuilder();
        foreach (var l in (r.StdOut + "\n" + r.StdErr).Split('\n'))
        {
            var line = l.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.StartsWith("github version", StringComparison.Ordinal))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }
        var rc = r.TimedOut ? -1 : r.ExitCode;
        // code 0 without the filter file would mean the engine exited before building the filter: not a pass
        if (rc == 0 && !filterSaved)
            rc = 1;
        return (rc, sb.ToString(), false);
    }
}
