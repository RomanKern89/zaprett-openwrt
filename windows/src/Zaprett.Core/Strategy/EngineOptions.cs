using Zaprett.Core.Config;
using Zaprett.Core.Text;

namespace Zaprett.Core.Strategy;

/// <summary>Argument kind of a long option (getopt no_argument / optional_argument / required_argument).</summary>
public enum OptionKind
{
    No,
    Optional,
    Required,
}

/// <summary>Long options of the engines. The engines parse argv with getopt_long_only: an option may be cut to any
/// unambiguous prefix, "-name" works like "--name", and an option with a required argument written without "=" takes the
/// NEXT word as its value, even one that starts with "-". The generator rewrites every option to
/// "--&lt;full name&gt;[=value]" first (the same algorithm as the router, strategy.uc canonicalize), so the reserved-option
/// and file checks see exactly what the engine will see.
///
/// Windows tables: long_options of zapret v72.13 nfq/nfqws.c and zapret2 1.0.5.2 nfq2/nfqws.c as compiled for
/// __CYGWIN__ (winws 115 options, winws2 77). Linux tables: the router's ENGINE_OPTIONS (strategy.uc). Options that
/// only the Linux engine knows (queue, user, marks...) are recognized so that a strategy written for the router still
/// loads here; the generator removes them with warning strategy_option_ignored.</summary>
public static class EngineOptions
{
    static Dictionary<string, OptionKind> Table(string no, string optional, string required)
    {
        var d = new Dictionary<string, OptionKind>(StringComparer.Ordinal);
        foreach (var (names, kind) in new[] { (no, OptionKind.No), (optional, OptionKind.Optional), (required, OptionKind.Required) })
            foreach (var n in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                d[n] = kind;
        return d;
    }

    static readonly Dictionary<string, OptionKind> WinwsTable = Table(
        "dry-run version daemon hostcase hostnospace domcase methodeol new skip",
        "debug comment synack-split ctrack-disable ipcache-hostname dup-autottl dup-autottl6 dup-tcp-flags-set dup-tcp-flags-unset " +
        "dup-replace orig-autottl orig-autottl6 orig-tcp-flags-set orig-tcp-flags-unset dpi-desync-autottl dpi-desync-autottl6 " +
        "dpi-desync-tcp-flags-set dpi-desync-tcp-flags-unset dpi-desync-skip-nosni dpi-desync-any-protocol nlm-list",
        "pidfile wsize wssize wssize-cutoff wssize-forced-cutoff ctrack-timeouts ipcache-lifetime hostspell ip-id dpi-desync dup dup-ttl " +
        "dup-ttl6 dup-fooling dup-ts-increment dup-badseq-increment dup-badack-increment dup-ip-id dup-start dup-cutoff orig-ttl orig-ttl6 " +
        "orig-mod-start orig-mod-cutoff dpi-desync-ttl dpi-desync-ttl6 dpi-desync-fooling dpi-desync-repeats dpi-desync-split-pos " +
        "dpi-desync-split-http-req dpi-desync-split-tls dpi-desync-split-seqovl dpi-desync-split-seqovl-pattern " +
        "dpi-desync-fakedsplit-pattern dpi-desync-fakedsplit-mod dpi-desync-hostfakesplit-midhost dpi-desync-hostfakesplit-mod " +
        "dpi-desync-ipfrag-pos-tcp dpi-desync-ipfrag-pos-udp dpi-desync-ts-increment dpi-desync-badseq-increment " +
        "dpi-desync-badack-increment dpi-desync-fake-tcp-mod dpi-desync-fake-http dpi-desync-fake-tls dpi-desync-fake-tls-mod " +
        "dpi-desync-fake-unknown dpi-desync-fake-syndata dpi-desync-fake-quic dpi-desync-fake-wireguard dpi-desync-fake-dht " +
        "dpi-desync-fake-discord dpi-desync-fake-stun dpi-desync-fake-unknown-udp dpi-desync-udplen-increment dpi-desync-udplen-pattern " +
        "dpi-desync-cutoff dpi-desync-start hostlist hostlist-domains hostlist-exclude hostlist-exclude-domains hostlist-auto " +
        "hostlist-auto-fail-threshold hostlist-auto-fail-time hostlist-auto-retrans-threshold hostlist-auto-debug filter-l3 filter-tcp " +
        "filter-udp filter-l7 ipset ipset-ip ipset-exclude ipset-exclude-ip wf-iface wf-l3 wf-tcp wf-udp wf-raw wf-raw-part " +
        "wf-filter-lan wf-save ssid-filter nlm-filter");

    static readonly Dictionary<string, OptionKind> Winws2Table = Table(
        "dry-run version daemon skip",
        "debug intercept comment chdir ctrack-disable payload-disable server ipcache-hostname reasm-disable writable " +
        "hostlist-auto-retrans-reset new template wf-tcp-empty wf-dup-check ssid-filter-neg nlm-filter-neg nlm-list",
        "fuzz pidfile ctrack-timeouts ipcache-lifetime blob lua-init lua-gc hostlist hostlist-domains hostlist-exclude " +
        "hostlist-exclude-domains hostlist-auto hostlist-auto-fail-threshold hostlist-auto-fail-time hostlist-auto-retrans-threshold " +
        "hostlist-auto-retrans-maxseq hostlist-auto-incoming-maxseq hostlist-auto-udp-in hostlist-auto-udp-out hostlist-auto-debug name " +
        "import cookie filter-l3 filter-tcp filter-udp filter-icmp filter-ipp filter-l7 ipset ipset-ip ipset-exclude ipset-exclude-ip " +
        "payload in-range out-range lua-desync wf-iface wf-l3 wf-tcp-in wf-tcp-out wf-udp-in wf-udp-out wf-icmp-in wf-icmp-out wf-ipp-in " +
        "wf-ipp-out wf-raw wf-raw-part wf-raw-filter wf-filter-lan wf-filter-loopback wf-save ssid-filter nlm-filter");

    // the router's tables (strategy.uc ENGINE_OPTION_NAMES, Linux build)
    static readonly Dictionary<string, OptionKind> NfqwsLinux = Table(
        "dry-run version daemon hostcase hostnospace domcase methodeol new skip bind-fix4 bind-fix6",
        "debug comment synack-split ctrack-disable ipcache-hostname dup-autottl dup-autottl6 dup-tcp-flags-set dup-tcp-flags-unset " +
        "dup-replace orig-autottl orig-autottl6 orig-tcp-flags-set orig-tcp-flags-unset dpi-desync-autottl dpi-desync-autottl6 " +
        "dpi-desync-tcp-flags-set dpi-desync-tcp-flags-unset dpi-desync-skip-nosni dpi-desync-any-protocol",
        "qnum pidfile user uid wsize wssize wssize-cutoff wssize-forced-cutoff ctrack-timeouts ipcache-lifetime hostspell ip-id " +
        "dpi-desync dpi-desync-fwmark dup dup-ttl dup-ttl6 dup-fooling dup-ts-increment dup-badseq-increment dup-badack-increment " +
        "dup-ip-id dup-start dup-cutoff orig-ttl orig-ttl6 orig-mod-start orig-mod-cutoff dpi-desync-ttl dpi-desync-ttl6 " +
        "dpi-desync-fooling dpi-desync-repeats dpi-desync-split-pos dpi-desync-split-http-req dpi-desync-split-tls " +
        "dpi-desync-split-seqovl dpi-desync-split-seqovl-pattern dpi-desync-fakedsplit-pattern dpi-desync-fakedsplit-mod " +
        "dpi-desync-hostfakesplit-midhost dpi-desync-hostfakesplit-mod dpi-desync-ipfrag-pos-tcp dpi-desync-ipfrag-pos-udp " +
        "dpi-desync-ts-increment dpi-desync-badseq-increment dpi-desync-badack-increment dpi-desync-fake-tcp-mod dpi-desync-fake-http " +
        "dpi-desync-fake-tls dpi-desync-fake-tls-mod dpi-desync-fake-unknown dpi-desync-fake-syndata dpi-desync-fake-quic " +
        "dpi-desync-fake-wireguard dpi-desync-fake-dht dpi-desync-fake-discord dpi-desync-fake-stun dpi-desync-fake-unknown-udp " +
        "dpi-desync-udplen-increment dpi-desync-udplen-pattern dpi-desync-cutoff dpi-desync-start hostlist hostlist-domains " +
        "hostlist-exclude hostlist-exclude-domains hostlist-auto hostlist-auto-fail-threshold hostlist-auto-fail-time " +
        "hostlist-auto-retrans-threshold hostlist-auto-debug filter-l3 filter-tcp filter-udp filter-l7 filter-ssid ipset ipset-ip " +
        "ipset-exclude ipset-exclude-ip");

    static readonly Dictionary<string, OptionKind> Nfqws2Linux = Table(
        "dry-run version bind-fix4 bind-fix6 daemon skip",
        "debug intercept comment chdir ctrack-disable payload-disable server ipcache-hostname reasm-disable writable " +
        "hostlist-auto-retrans-reset new template filter-ssid-neg",
        "fuzz qnum pidfile user uid ctrack-timeouts ipcache-lifetime fwmark blob lua-init lua-gc hostlist hostlist-domains " +
        "hostlist-exclude hostlist-exclude-domains hostlist-auto hostlist-auto-fail-threshold hostlist-auto-fail-time " +
        "hostlist-auto-retrans-threshold hostlist-auto-retrans-maxseq hostlist-auto-incoming-maxseq hostlist-auto-udp-in " +
        "hostlist-auto-udp-out hostlist-auto-debug name import cookie filter-l3 filter-tcp filter-udp filter-icmp filter-ipp filter-l7 " +
        "filter-ssid filter-mark ipset ipset-ip ipset-exclude ipset-exclude-ip payload in-range out-range lua-desync");

    /// <summary>Options of the Windows engine.</summary>
    public static IReadOnlyDictionary<string, OptionKind> Windows(string engine) => engine == Engines.Winws2 ? Winws2Table : WinwsTable;

    static readonly Dictionary<string, OptionKind> WinwsAll = Union(WinwsTable, NfqwsLinux);
    static readonly Dictionary<string, OptionKind> Winws2All = Union(Winws2Table, Nfqws2Linux);

    static Dictionary<string, OptionKind> Union(Dictionary<string, OptionKind> win, Dictionary<string, OptionKind> linux)
    {
        var d = new Dictionary<string, OptionKind>(win, StringComparer.Ordinal);
        foreach (var (k, v) in linux)
            d.TryAdd(k, v);
        return d;
    }

    /// <summary>All recognized options of an engine: the Windows ones and the router-only ones.</summary>
    public static IReadOnlyDictionary<string, OptionKind> Known(string engine) => engine == Engines.Winws2 ? Winws2All : WinwsAll;

    /// <summary>An option only the Linux (router) engine knows: removed from a strategy on Windows.</summary>
    public static bool IsLinuxOnly(string engine, string name) => Known(engine).ContainsKey(name) && !Windows(engine).ContainsKey(name);

    /// <summary>Full name as getopt_long_only resolves it: exact name, else the only option starting with it.</summary>
    public static (string? Name, IReadOnlyList<string> Ambiguous) Resolve(string name, IReadOnlyDictionary<string, OptionKind> known)
    {
        if (name.Length == 0)
            return (null, []);
        if (known.ContainsKey(name))
            return (name, []);
        var hits = known.Keys.Where(k => k.StartsWith(name, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        return hits.Count == 1 ? (hits[0], []) : (null, hits);
    }

    /// <summary>Rewrites every option to "--&lt;full name&gt;[=value]"; a required argument written without "=" takes the next
    /// word. ${hostlists} and ${ipsets} stay. Refused (error text): words that are not options, unknown and ambiguous
    /// names, a value for an option without argument, a required argument missing at the end or followed by a list
    /// placeholder.</summary>
    public static (List<string>? Tokens, string? Error) Canonicalize(IReadOnlyList<string> tokens, string engine)
    {
        var known = Known(engine);
        var o = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t is "${hostlists}" or "${ipsets}")
            {
                o.Add(t);
                continue;
            }
            // ^--?([^=]*)(=.*)?$ as the router
            if (!t.StartsWith('-'))
                return (null, T.S("gen.not_an_option", t));
            var body = t.StartsWith("--", StringComparison.Ordinal) ? t[2..] : t[1..];
            var eq = body.IndexOf('=', StringComparison.Ordinal);
            var name = eq >= 0 ? body[..eq] : body;
            string? value = eq >= 0 ? body[eq..] : null;
            var (full, ambiguous) = Resolve(name, known);
            if (full == null)
                return (null, ambiguous.Count > 1
                    ? T.S("gen.ambiguous_option", name, engine)
                    : T.S("gen.unknown_option", engine, name));
            var kind = known[full];
            if (kind == OptionKind.No && value != null)
                return (null, T.S("gen.option_no_value", full, t));
            if (kind == OptionKind.Required && value == null)
            {
                var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                if (next is null or "${hostlists}" or "${ipsets}")
                    return (null, T.S("gen.option_needs_value", full));
                value = "=" + next;
                i++;
            }
            o.Add("--" + full + (value ?? ""));
        }
        return (o, null);
    }
}
