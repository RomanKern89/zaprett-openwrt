using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Presets;
using Zaprett.Core.Util;

namespace Zaprett.Core.Checks;

public sealed record TargetResult(string Url, bool Ok, long? Ms, long Bytes, int? HttpStatus, string? Error, string? Detail)
{
    /// <summary>{url, ok, ms, bytes, error} as probe/monitor/test answers carry it.</summary>
    public JsonObject ToJson(bool withHttp = false)
    {
        var o = new JsonObject { ["url"] = Url, ["ok"] = Ok, ["ms"] = Ms, ["bytes"] = Bytes, ["error"] = Ok ? null : Error };
        if (withHttp)
        {
            o["http_status"] = HttpStatus;
            o["detail"] = Ok ? null : Detail;
        }
        return o;
    }
}

public sealed record ProbeSummary(int Ok, int Total, long? AvgMs, IReadOnlyList<TargetResult> Targets);

/// <summary>Checks of targets through <see cref="IHttpProbe"/> (router tester.probe_targets): parallel with a limit,
/// every result normalized to the router semantics (<see cref="ProbeErrors.Normalize"/>).</summary>
public static class ProbeRunner
{
    public static async Task<ProbeSummary> ProbeTargetsAsync(IHttpProbe http, IReadOnlyList<Target> targets, int concurrency,
        int timeoutSec, (int From, int To)? localPorts, CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = targets.Select(async t =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ProbeOneAsync(http, t, timeoutSec, localPorts, ct).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var ok = results.Count(r => r.Ok);
        var ms = results.Where(r => r.Ok && r.Ms != null).Select(r => r.Ms!.Value).ToList();
        return new ProbeSummary(ok, targets.Count, ms.Count > 0 ? ms.Sum() / ms.Count : null, results);
    }

    public static async Task<TargetResult> ProbeOneAsync(IHttpProbe http, Target t, int timeoutSec, (int From, int To)? localPorts,
        CancellationToken ct)
    {
        try
        {
            var req = new ProbeRequest(t.Url, t.MinBytes, TimeSpan.FromSeconds(timeoutSec), localPorts?.From, localPorts?.To);
            var r = ProbeErrors.Normalize(await http.ProbeAsync(req, ct).ConfigureAwait(false), t.MinBytes);
            return new TargetResult(t.Url, r.Ok, r.Ms, r.Bytes, r.HttpStatus, r.Ok ? null : r.Error, r.Ok ? null : ProbeErrors.TextOf(r.Error));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var code = ProbeErrors.Classify(e);
            return new TargetResult(t.Url, false, null, 0, ProbeErrors.HttpStatus(e), code, Trim(e.Message));
        }
    }

    static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    /// <summary>Unique targets of the services (at most limit).</summary>
    public static List<Target> UniqueTargets(IEnumerable<ServiceTargets> services, int? limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var o = new List<Target>();
        foreach (var s in services)
            foreach (var t in s.Targets)
            {
                if (limit != null && o.Count >= limit)
                    return o;
                if (seen.Add(t.Url))
                    o.Add(t);
            }
        return o;
    }

    /// <summary>Splits one probe over unique URLs into per-service results (router health.per_service).</summary>
    public static (int Ok, int Total, JsonArray Services) PerService(IEnumerable<ServiceTargets> services, ProbeSummary res)
    {
        var byUrl = res.Targets.GroupBy(t => t.Url).ToDictionary(g => g.Key, g => g.First());
        var o = new JsonArray();
        int okc = 0, total = 0;
        foreach (var s in services)
        {
            var tg = new JsonArray();
            int sok = 0;
            long ms = 0;
            var msn = 0;
            foreach (var t in s.Targets)
            {
                var r = byUrl.GetValueOrDefault(t.Url) ?? new TargetResult(t.Url, false, null, 0, null, "failed", null);
                tg.Add(r.ToJson());
                if (!r.Ok)
                    continue;
                sok++;
                if (r.Ms != null)
                {
                    ms += r.Ms.Value;
                    msn++;
                }
            }
            okc += sok;
            total += tg.Count;
            o.Add(new JsonObject
            {
                ["id"] = s.Id, ["name"] = s.Name, ["ok"] = sok, ["total"] = tg.Count, ["avg_ms"] = msn > 0 ? ms / msn : null, ["targets"] = tg,
            });
        }
        return (okc, total, o);
    }

    public static JsonObject SummaryJson(ProbeSummary s) => new()
    {
        ["ok"] = s.Ok, ["total"] = s.Total, ["avg_ms"] = s.AvgMs, ["targets"] = R.Arr(s.Targets.Select(t => (JsonNode?)t.ToJson(true))),
    };
}
