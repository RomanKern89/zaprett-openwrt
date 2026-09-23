using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zaprett.Core.Config;
using Zaprett.Core.Engine;
using Zaprett.Core.Jobs;
using Zaprett.Core.Presets;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;
using Zaprett.Core.Text;

namespace Zaprett.Core.Checks;

/// <summary>One target as the judge sees it. Tcp: null = not checked, true/false = a TCP connection to an address from
/// DoH was (not) established.</summary>
public sealed record DiagInput(IReadOnlyList<string> Sys, IReadOnlyList<string>? Doh, TargetResult Fetch, bool? Tcp);

public sealed record Verdict(string Name, bool Spoofed, bool NeedTcp, string? Reason, string? Error);

/// <summary>"How does the provider block it" (router diagnose.uc, contract v1.4 §15.4): system DNS against
/// DNS-over-HTTPS, the page download as usual, and a TCP check of the real address when the download fails. The engine
/// is neither stopped nor restarted. Differences from the router: the platform probe gives no body, so a provider stub
/// page is recognized only by HTTP 451 or a forged certificate.</summary>
public sealed partial class Diagnose
{
    public static readonly IReadOnlyList<string> Verdicts = ["ok", "dns_spoof", "ip_block", "tls_block", "throttle", "http_block", "unknown"];

    /// <summary>The known freeze of TLS connections after 14–24 KB: a download that broke off after the body had started and
    /// before this many bytes counts as throttling.</summary>
    public const long ThrottleMax = 24576;
    public static readonly IReadOnlyList<string> ThrottleErrors = ["timeout", "reset", "tls_error", "failed"];
    public const int MaxTargets = 20;
    public const int TcpCheckIps = 2;

    /// <summary>Reasons with a text (resource key diagnose.reason.&lt;reason&gt;).</summary>
    public static readonly IReadOnlyList<string> ReasonsWithText =
    [
        "stub_address", "no_system_answer", "address_differs", "dns_failed", "http_451", "forged_certificate", "tcp_failed",
        "tcp_ok_fetch_failed", "tls_error", "reset", "timeout", "tcp_not_checked",
    ];

    [GeneratedRegex("^(https?)://([A-Za-z0-9.-]+)(:([0-9]+))?(/.*)?$")]
    private static partial Regex UrlParts();

    readonly CoreContext c;
    readonly EngineService engine;

    public Diagnose(CoreContext context, EngineService engine)
    {
        c = context;
        this.engine = engine;
    }

    public string ResultPath => c.RunFile("diagnose.json");

    public static (string Host, int Port)? UrlHost(string? url)
    {
        var m = UrlParts().Match(url ?? "");
        if (!m.Success)
            return null;
        var port = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? int.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)
            : m.Groups[1].Value == "https" ? 443 : 80;
        return (m.Groups[2].Value.ToLowerInvariant(), port);
    }

    /// <summary>An address no real public site has (a provider stub of the system DNS points at such addresses).</summary>
    public static bool IsStub(string ip)
    {
        var p = Validate.Ipv4Parse(ip);
        if (p == null)
            return false;
        int a = p[0], b = p[1];
        return a == 0 || a == 10 || a == 127 || a >= 224 || (a == 100 && b >= 64 && b <= 127) || (a == 169 && b == 254) ||
            (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
    }

    /// <summary>Verdict of one target; NeedTcp asks the caller to check TCP and judge again.</summary>
    public static Verdict Judge(DiagInput t)
    {
        var f = t.Fetch;
        var doh = t.Doh ?? [];
        Verdict Res(string v, bool sp, string? reason) => new(v, sp, false, reason, f.Ok ? null : f.Error);
        if (t.Sys.Count > 0 && t.Sys.All(IsStub))
            return Res("dns_spoof", true, "stub_address");
        if (f.Ok)
            return Res("ok", false, null);
        if (t.Sys.Count == 0 && doh.Count > 0)
            return Res("dns_spoof", true, "no_system_answer");
        if (t.Sys.Count > 0 && doh.Count > 0 && !t.Sys.Any(doh.Contains))
            return Res("dns_spoof", true, "address_differs");
        if (t.Sys.Count == 0 && t.Doh == null)
            return Res("unknown", false, "dns_failed");
        if (f.HttpStatus == 451)
            return Res("http_block", false, "http_451");
        if (f.Error == "tls_cert")
            return Res("http_block", false, "forged_certificate");
        if (f.Bytes > 0 && f.Bytes <= ThrottleMax && ThrottleErrors.Contains(f.Error ?? ""))
            return Res("throttle", false, "stalled_" + f.Error);
        if (f.Error == "tls_error")
            return Res("tls_block", false, "tls_error");
        if (f.Error is "connect_failed" or "reset" or "timeout")
        {
            if (t.Tcp == null)
                return Res("unknown", false, "tcp_not_checked") with { NeedTcp = true };
            if (t.Tcp == false)
                return Res("ip_block", false, "tcp_failed");
            return f.Error == "connect_failed" ? Res("unknown", false, "tcp_ok_fetch_failed") : Res("tls_block", false, f.Error);
        }
        return Res("unknown", false, f.Error);
    }

    /// <summary>summary.verdict: the most frequent verdict other than ok (ties: order of <see cref="Verdicts"/>), or ok.</summary>
    public static (string Verdict, JsonObject Counts) Summarize(IEnumerable<string> verdicts)
    {
        var counts = new Dictionary<string, int>();
        foreach (var v in verdicts)
            counts[v] = counts.GetValueOrDefault(v) + 1;
        var best = "ok";
        var n = 0;
        foreach (var v in Verdicts)
            if (v != "ok" && counts.GetValueOrDefault(v) > n)
            {
                best = v;
                n = counts[v];
            }
        var o = new JsonObject();
        foreach (var (k, v) in counts)
            o[k] = v;
        return (best, o);
    }

    static string Detail(Verdict j, DiagInput t)
    {
        var s = j.Reason != null && ReasonsWithText.Contains(j.Reason) ? T.S("diagnose.reason." + j.Reason) :
            j.Reason != null ? T.S("diagnose.download_error", ProbeErrors.TextOf(j.Reason)) : T.S("diagnose.site_opens");
        if (j.Name == "throttle")
            s = T.S("diagnose.throttle", t.Fetch.Bytes);
        return T.S("diagnose.detail", s, t.Sys.Count > 0 ? string.Join(", ", t.Sys) : "—",
            t.Doh == null ? T.S("diagnose.no_answer") : t.Doh.Count > 0 ? string.Join(", ", t.Doh) : "—");
    }

    async Task<IReadOnlyList<string>> SystemAsync(string host, CancellationToken ct)
    {
        try
        {
            return (await c.P.Dns.ResolveSystemAsync(host, ct).ConfigureAwait(false)).Where(Validate.Ipv4Valid).Distinct().ToList();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            c.P.Log.Info(T.S("diagnose.log_system_dns", host, e.Message));
            return [];
        }
    }

    async Task<IReadOnlyList<string>?> DohAsync(string host, CancellationToken ct)
    {
        try
        {
            return (await c.P.Dns.ResolveDohAsync(host, ct).ConfigureAwait(false)).Where(Validate.Ipv4Valid).Distinct().ToList();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            c.P.Log.Info(T.S("diagnose.log_doh", host, e.Message));
            return null;
        }
    }

    public async Task<JsonObject> RunAsync(ZaprettConfig cfg, IReadOnlyList<string>? ids, JobContext ctx)
    {
        var (services, unknown) = PresetLogic.PresetServices(c.LoadPresets(), cfg, ids);
        if (services == null)
            return R.Fail("unknown_service", T.S("health.unknown_service", unknown));
        var targets = new List<(Target T, string Host, int Port, string Service)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in services)
            foreach (var t in s.Targets)
                if (UrlHost(t.Url) is { } u && targets.Count < MaxTargets && seen.Add(t.Url))
                    targets.Add((t, u.Host, u.Port, s.Id));
        if (targets.Count == 0)
            return R.Fail("no_targets", T.S("diagnose.no_targets"));
        var ct = ctx.Token;
        var res = new JsonObject { ["started"] = c.Now, ["finished"] = 0, ["engine_running"] = engine.Running };

        ctx.Progress(5, T.S("diagnose.progress_dns", targets.Count));
        var dns = new Dictionary<string, (IReadOnlyList<string> Sys, IReadOnlyList<string>? Doh)>();
        foreach (var t in targets)
        {
            if (dns.ContainsKey(t.Host))
                continue;
            dns[t.Host] = Validate.Ipv4Valid(t.Host)
                ? ([t.Host], [t.Host])
                : (await SystemAsync(t.Host, ct).ConfigureAwait(false), await DohAsync(t.Host, ct).ConfigureAwait(false));
        }

        ctx.Progress(35, T.S("diagnose.progress_pages"));
        var fetched = await ProbeRunner.ProbeTargetsAsync(c.P.Http, targets.Select(t => t.T).ToList(), cfg.Test.Concurrency,
            cfg.Test.Timeout, null, ct).ConfigureAwait(false);
        var inputs = new List<DiagInput>();
        for (var i = 0; i < targets.Count; i++)
        {
            var d = dns[targets[i].Host];
            var input = new DiagInput(d.Sys, d.Doh, fetched.Targets[i], null);
            if (Judge(input).NeedTcp)
            {
                ctx.Progress(70, T.S("diagnose.progress_tcp", targets[i].Host));
                // the addresses the PC really used first (system answer confirmed by DoH), then the other DoH ones
                var ips = (d.Doh ?? []).Where(d.Sys.Contains).Concat((d.Doh ?? []).Where(ip => !d.Sys.Contains(ip))).Take(TcpCheckIps).ToList();
                bool? tcp = false;
                foreach (var ip in ips)
                {
                    try
                    {
                        if (await c.P.Dns.TcpConnectAsync(ip, targets[i].Port, TimeSpan.FromSeconds(cfg.Test.Timeout), ct).ConfigureAwait(false))
                        {
                            tcp = true;
                            break;
                        }
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        c.P.Log.Info(T.S("diagnose.log_tcp", ip, targets[i].Port, e.Message));
                    }
                }
                input = input with { Tcp = tcp };
            }
            inputs.Add(input);
        }

        var outTargets = new JsonArray();
        var verdicts = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var st = inputs[i];
            var j = Judge(st);
            verdicts.Add(j.Name);
            outTargets.Add(new JsonObject
            {
                ["url"] = t.T.Url, ["host"] = t.Host, ["service"] = t.Service, ["verdict"] = j.Name,
                ["dns"] = new JsonObject { ["system"] = R.Arr(st.Sys), ["doh"] = R.Arr(st.Doh ?? []), ["spoofed"] = j.Spoofed },
                ["detail"] = Detail(j, st), ["reason"] = j.Reason, ["error"] = j.Error, ["bytes"] = st.Fetch.Bytes, ["tcp"] = st.Tcp,
            });
            ctx.Log(T.S("diagnose.log_target", t.T.Url, j.Name, j.Reason ?? ""));
        }
        var (verdict, counts) = Summarize(verdicts);
        res["targets"] = outTargets;
        res["summary"] = new JsonObject { ["verdict"] = verdict, ["counts"] = counts };
        res["finished"] = c.Now;
        if (!Files.WriteJson(ResultPath, res))
            return R.Fail("write_failed", T.S("health.write_failed", ResultPath));
        return R.Ok(new JsonObject
        {
            ["verdict"] = verdict, ["counts"] = counts.DeepClone(), ["total"] = targets.Count,
            ["message"] = T.S("diagnose.result", verdict, targets.Count),
        });
    }

    public JsonObject Status() => R.Ok(new JsonObject { ["diagnose"] = Files.ReadJson(ResultPath) });
}
