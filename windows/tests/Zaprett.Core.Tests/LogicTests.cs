using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using Zaprett.Core.Checks;
using Zaprett.Core.Config;
using Zaprett.Core.Platform;
using Zaprett.Core.Presets;
using Zaprett.Core.Store;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Pure logic: presets, error classifier, monitor, watchdog decision, verdicts of the diagnosis, tester ranking.</summary>
public sealed class LogicTests
{
    static JsonObject Presets() => PresetLogic.Load(RepoPaths.PresetsFile)!;

    static ZaprettConfig Cfg(JsonObject? main = null, JsonObject? extra = null)
    {
        var doc = new JsonObject { ["main"] = main ?? new JsonObject() };
        if (extra != null)
            foreach (var (k, v) in extra)
                doc[k] = v?.DeepClone();
        return ConfigLoader.Normalize(doc);
    }

    [Theory]
    [InlineData("youtube", "youtube", null)]
    [InlineData("discord:voice", "discord", "voice")]
    public void ParseServiceRef(string r, string id, string? v)
    {
        var p = PresetLogic.ParseServiceRef(r)!.Value;
        Assert.Equal(id, p.Id);
        Assert.Equal(v, p.Variant);
    }

    [Theory]
    [InlineData("a:b:c")]
    [InlineData("bad id")]
    [InlineData(":x")]
    public void ParseServiceRef_Bad(string r) => Assert.Null(PresetLogic.ParseServiceRef(r));

    [Fact]
    public void Sets_Variants_Active()
    {
        var p = Presets();
        var discord = PresetLogic.Services(p).First(s => R.Str(s["id"]) == "discord");
        var sets = PresetLogic.Sets(discord);
        Assert.Null(sets[0].Id);
        Assert.Equal(["full", "voice"], sets.Skip(1).Select(s => s.Id));
        var cfg = Cfg(new JsonObject { ["lists"] = new JsonArray("zaprett-discord-full") });
        var srcs = cfg.Sources.ToDictionary(s => s.Name);
        Assert.Equal("full", PresetLogic.EnabledVariant(discord, cfg, srcs));
        Assert.True(PresetLogic.ServiceActive(discord, cfg));
        Assert.Null(PresetLogic.EnabledVariant(discord, Cfg(), srcs));
        var cf = PresetLogic.Services(p).First(s => R.Str(s["id"]) == "cloudflare");
        var withSrc = Cfg(new JsonObject { ["ipsets"] = new JsonArray("src-cloudflare_v4", "src-cloudflare_v6") },
            new JsonObject { ["sources"] = new JsonObject
            {
                ["cloudflare_v4"] = new JsonObject { ["enabled"] = true, ["type"] = "ipset", ["url"] = "https://www.cloudflare.com/ips-v4" },
                ["cloudflare_v6"] = new JsonObject { ["enabled"] = true, ["type"] = "ipset", ["url"] = "https://www.cloudflare.com/ips-v6" },
            } });
        Assert.Equal(2, PresetLogic.SetActive(PresetLogic.Sets(cf)[0], withSrc, withSrc.Sources.ToDictionary(s => s.Name)));
        Assert.True(PresetLogic.ServiceActive(cf, withSrc));
    }

    [Fact]
    public void Targets_FromServicesAndLists()
    {
        var p = Presets();
        var cfg = Cfg();
        var (svc, unknown) = PresetLogic.PresetServices(p, cfg, null);
        Assert.Null(unknown);
        Assert.Equal(["youtube", "discord"], svc!.Select(s => s.Id));
        Assert.Equal("nope", PresetLogic.PresetServices(p, cfg, ["nope"]).UnknownId);
        var t = PresetLogic.BuildTargets(p, cfg, ["# c\n^strict.example\nwww.a.com\nwww.a.com\n1.2.3.4\nnodot\nun_der.com\nb.com\n"], 2);
        Assert.Contains(t, x => x.Url == "https://www.youtube.com/" && x.MinBytes == 131072);
        Assert.Equal(["https://strict.example/", "https://www.a.com/"], t.Where(x => x.Service == null).Select(x => x.Url));
    }

    [Fact]
    public void MemoryAndDns()
    {
        var p = Presets();
        var rkn = Cfg(new JsonObject { ["lists"] = new JsonArray("src-refilter_domains") });
        var heavy = new List<SourceConfig> { ConfigLoader.NormalizeSource("x", new JsonObject
        {
            ["enabled"] = true, ["url"] = "https://e.com/", ["ram_mib"] = 600,
        }) };
        Assert.True(PresetLogic.MemoryHeavy(Cfg(), p, heavy, null, 1000));
        Assert.False(PresetLogic.MemoryHeavy(Cfg(), p, heavy, null, 2000));
        Assert.False(PresetLogic.MemoryHeavy(Cfg(), p, heavy, null, null));
        var rknFull = Cfg(new JsonObject { ["lists"] = new JsonArray("src-refilter_domains") });
        Assert.True(PresetLogic.MemoryHeavy(rknFull, p, [], 128, null));
        Assert.True(PresetLogic.DnsPlain(Cfg(new JsonObject { ["lists"] = new JsonArray("zaprett-rutracker") }), p, false));
        Assert.False(PresetLogic.DnsPlain(Cfg(new JsonObject { ["lists"] = new JsonArray("zaprett-rutracker") }), p, true));
        Assert.False(PresetLogic.DnsPlain(Cfg(), p, false));
        Assert.Equal("z2-general", PresetLogic.DefaultStrategy(p, "winws2"));
        Assert.Equal("strategy-general", PresetLogic.DefaultStrategy(p, "winws"));
        Assert.True(PresetLogic.ServiceActive(PresetLogic.Services(p).First(s => R.Str(s["id"]) == "rkn_full"), rkn));
    }

    [Theory]
    [InlineData(true, 200000, 131072, null, null, true, null)]
    [InlineData(true, 100, 131072, null, null, false, "too_small")]
    [InlineData(false, 0, 0, "http_error", 404, true, null)]
    [InlineData(false, 0, 10, "http_error", 404, false, "http_error")]
    [InlineData(false, 0, 0, "weird", null, false, "failed")]
    [InlineData(false, 0, 0, null, null, false, "failed")]
    [InlineData(false, 0, 0, "reset", null, false, "reset")]
    public void Normalize(bool ok, long bytes, long min, string? err, int? http, bool wantOk, string? wantErr)
    {
        var r = ProbeErrors.Normalize(new ProbeResult("u", ok, 1, bytes, err, http), min);
        Assert.Equal(wantOk, r.Ok);
        Assert.Equal(wantErr, r.Error);
    }

    [Fact]
    public void Classify()
    {
        Assert.Equal("timeout", ProbeErrors.Classify(new TaskCanceledException()));
        Assert.Equal("timeout", ProbeErrors.Classify(new TimeoutException()));
        Assert.Equal("http_error", ProbeErrors.Classify(new HttpRequestException("x", null, HttpStatusCode.Forbidden)));
        Assert.Equal(403, ProbeErrors.HttpStatus(new HttpRequestException("x", null, HttpStatusCode.Forbidden)));
        Assert.Equal("reset", ProbeErrors.Classify(new HttpRequestException("x", new IOException("y", new SocketException((int)SocketError.ConnectionReset)))));
        Assert.Equal("connect_failed", ProbeErrors.Classify(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Equal("tls_cert", ProbeErrors.Classify(new HttpRequestException("x", new AuthenticationException("The remote certificate is invalid"))));
        Assert.Equal("tls_error", ProbeErrors.Classify(new AuthenticationException("handshake failed")));
        Assert.Equal("connect_failed", ProbeErrors.Classify(new HttpRequestException(HttpRequestError.NameResolutionError, "dns")));
        Assert.Equal("tls_error", ProbeErrors.Classify(new HttpRequestException(HttpRequestError.SecureConnectionError, "tls")));
        Assert.Equal("reset", ProbeErrors.Classify(new HttpRequestException(HttpRequestError.ResponseEnded, "end")));
        Assert.Equal("failed", ProbeErrors.Classify(new HttpRequestException("other")));
        Assert.Equal("timeout", ProbeErrors.Classify(new SocketException((int)SocketError.TimedOut)));
        Assert.Equal("local_error", ProbeErrors.Classify(new UnauthorizedAccessException()));
        Assert.Equal("local_error", ProbeErrors.Classify(new IOException("disk full")));
        Assert.Equal("too_large", ProbeErrors.Classify(new DownloadException("too_large", "big")));
        Assert.Equal("failed", ProbeErrors.Classify(new DownloadException("strange", "?")));
        Assert.Equal("failed", ProbeErrors.Classify(new InvalidOperationException()));
        Assert.Equal("нет ответа (таймаут)", ProbeErrors.TextOf("timeout"));
        Assert.Equal("zzz", ProbeErrors.TextOf("zzz"));
    }

    static readonly MonitorConfig Mon = new(true, true, 30, 2, true, 5, 8);

    [Fact]
    public void MonitorStep_ThresholdRepairHistory()
    {
        var (s1, r1) = MonitorLogic.Step(null, 1, 4, 1000, Mon);
        Assert.Equal("ok", R.Str(s1["state"]));
        Assert.Equal(1L, R.Long(s1["consecutive_failures"]));
        Assert.False(r1);
        var (s2, r2) = MonitorLogic.Step(s1, 1, 4, 2000, Mon);
        Assert.Equal("degraded", R.Str(s2["state"]));
        Assert.True(r2);
        s2["last_repair"] = new JsonObject { ["t"] = 2000, ["job_id"] = "j" };
        var (s3, r3) = MonitorLogic.Step(s2, 0, 4, 2000 + 3600, Mon);
        Assert.False(r3);
        var (_, r4) = MonitorLogic.Step(s3, 0, 4, 2000 + MonitorLogic.RepairInterval, Mon);
        Assert.True(r4);
        var (s5, _) = MonitorLogic.Step(s3, 2, 4, 9000, Mon);
        Assert.Equal("ok", R.Str(s5["state"]));
        Assert.Equal(0L, R.Long(s5["consecutive_failures"]));
        var (s6, r6) = MonitorLogic.Step(s5, 0, 0, 9100, Mon);
        Assert.Equal("unknown", R.Str(s6["state"]));
        Assert.False(r6);
        JsonObject? st = null;
        for (var i = 0; i < 60; i++)
            st = MonitorLogic.Step(st, 1, 1, i, Mon).State;
        Assert.Equal(MonitorLogic.History, ((JsonArray)st!["history"]!).Count);
        Assert.Equal(12L, R.Long(((JsonArray)st["history"]!)[0]!["t"]));
        Assert.False(MonitorLogic.Step(s1, 0, 4, 5000, Mon with { AutoRepair = false }).Repair);
    }

    [Fact]
    public void MonitorSkipAndEnsure()
    {
        var on = Cfg(new JsonObject { ["enabled"] = true });
        Assert.Equal("monitor_disabled", MonitorLogic.SkipReason(Cfg(extra: new JsonObject { ["monitor"] = new JsonObject { ["enabled"] = false } }), false, true, false));
        Assert.Equal("service_disabled", MonitorLogic.SkipReason(Cfg(), false, true, false));
        Assert.Equal("stopped", MonitorLogic.SkipReason(on, true, true, false));
        Assert.Equal("not_running", MonitorLogic.SkipReason(on, false, false, false));
        Assert.Equal("job_busy", MonitorLogic.SkipReason(on, false, true, true));
        Assert.Null(MonitorLogic.SkipReason(on, false, true, false));
        Assert.Equal(("none", "disabled"), MonitorLogic.EnsureDecision(Cfg(), false, false, false, true));
        Assert.Equal(("none", "stopped"), MonitorLogic.EnsureDecision(on, true, false, false, true));
        Assert.Equal(("skipped", "test_running"), MonitorLogic.EnsureDecision(on, false, true, false, true));
        Assert.Equal(("started", "not_running"), MonitorLogic.EnsureDecision(on, false, false, false, true));
        Assert.Equal(("fw_applied", (string?)null), MonitorLogic.EnsureDecision(on, false, false, true, false));
        Assert.Equal(("none", (string?)null), MonitorLogic.EnsureDecision(on, false, false, true, true));
    }

    static TargetResult F(bool ok, string? err = null, long bytes = 0, int? http = null) => new("https://x/", ok, 10, bytes, http, err, null);

    [Fact]
    public void Judge_Verdicts()
    {
        Assert.Equal("dns_spoof", Diagnose.Judge(new(["10.10.10.10"], ["1.1.1.1"], F(true), null)).Name);
        Assert.Equal("ok", Diagnose.Judge(new(["8.8.4.4"], ["1.1.1.1"], F(true), null)).Name);
        var noSys = Diagnose.Judge(new([], ["1.1.1.1"], F(false, "connect_failed"), null));
        Assert.Equal(("dns_spoof", "no_system_answer"), (noSys.Name, noSys.Reason));
        Assert.Equal("address_differs", Diagnose.Judge(new(["2.2.2.2"], ["1.1.1.1"], F(false, "timeout"), null)).Reason);
        Assert.Equal("dns_failed", Diagnose.Judge(new([], null, F(false, "connect_failed"), null)).Reason);
        Assert.Equal("http_451", Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "http_error", 0, 451), null)).Reason);
        Assert.Equal("forged_certificate", Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "tls_cert"), null)).Reason);
        var thr = Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "timeout", 16000), null));
        Assert.Equal(("throttle", "stalled_timeout"), (thr.Name, thr.Reason));
        Assert.Equal("tls_block", Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "tls_error"), null)).Name);
        Assert.True(Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "reset"), null)).NeedTcp);
        Assert.Equal("ip_block", Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "connect_failed"), false)).Name);
        Assert.Equal("tls_block", Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "reset"), true)).Name);
        Assert.Equal("tcp_ok_fetch_failed", Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "connect_failed"), true)).Reason);
        Assert.Equal("unknown", Diagnose.Judge(new(["1.1.1.1"], ["1.1.1.1"], F(false, "local_error"), null)).Name);
        Assert.True(Diagnose.IsStub("100.64.1.1"));
        Assert.False(Diagnose.IsStub("100.128.1.1"));
        Assert.False(Diagnose.IsStub("not-ip"));
        Assert.Equal(("h.com", 8443), Diagnose.UrlHost("https://H.com:8443/x"));
        Assert.Equal(("h.com", 80), Diagnose.UrlHost("http://h.com"));
        Assert.Null(Diagnose.UrlHost("ftp://x"));
        var (v, counts) = Diagnose.Summarize(["ok", "throttle", "ip_block", "ip_block", "throttle"]);
        Assert.Equal("ip_block", v);
        Assert.Equal(2L, R.Long(counts["throttle"]));
        Assert.Equal("ok", Diagnose.Summarize(["ok"]).Verdict);
    }

    static JsonObject Res(string id, string status, int ok, double ratio, long? ms) =>
        new() { ["id"] = id, ["status"] = status, ["ok"] = ok, ["ratio"] = ratio, ["avg_ms"] = ms };

    [Fact]
    public void Tester_SortPickTrim()
    {
        var sorted = Tester.SortResults([Res("c", "invalid", 0, 0, null), Res("b", "done", 2, 0.5, 100), Res("a", "done", 2, 0.5, 50), Res("d", "done", 4, 1, 900)]);
        Assert.Equal(["d", "a", "b", "c"], sorted.Select(r => R.Str(r["id"])));
        Assert.Equal("d", Tester.PickBetter(sorted, "a"));
        Assert.Null(Tester.PickBetter(sorted, "d"));
        Assert.Null(Tester.PickBetter([Res("x", "done", 1, 0.5, 1), Res("o", "done", 1, 0.5, 2)], "o"));
        Assert.Equal("x", Tester.PickBetter([Res("x", "done", 1, 0.5, 1), Res("o", "invalid", 0, 0, null)], "o"));
        Assert.Null(Tester.PickBetter([Res("x", "done", 0, 0, 1)], "o"));
        Assert.Equal(0.75, Tester.Ratio(3, 4));
        Assert.Equal(0, Tester.Ratio(0, 0));
        var big = new JsonObject { ["results"] = new JsonArray(), ["baseline"] = new JsonObject { ["targets"] = new JsonArray() } };
        for (var i = 0; i < 12; i++)
        {
            var t = new JsonArray();
            for (var k = 0; k < 120; k++)
                t.Add(k);
            ((JsonArray)big["results"]!).Add(new JsonObject { ["id"] = "s" + i, ["targets"] = t });
        }
        for (var k = 0; k < 120; k++)
            ((JsonArray)big["baseline"]!["targets"]!).Add(k);
        var tr = Tester.Trim(big.DeepClone().AsObject())!;
        Assert.True(R.Bool(tr["targets_trimmed"]));
        Assert.Equal(100, ((JsonArray)tr["results"]![0]!["targets"]!).Count);
        Assert.Null(tr["results"]![11]!["targets"]);
        Assert.Equal(100, ((JsonArray)tr["baseline"]!["targets"]!).Count);
        var br = Tester.Brief(big.DeepClone().AsObject())!;
        Assert.Null(br["results"]![0]!["targets"]);
        Assert.Null(br["baseline"]!["targets"]);
        Assert.Null(Tester.Brief(null));
    }

    [Fact]
    public void Tester_CandidatesAndRefusal()
    {
        using var p = new TestPaths();
        var idx = new ItemStore(p).Scan();
        var presets = Presets();
        var cfg = Cfg(new JsonObject { ["strategy"] = "strategy-alt" });
        var (quick, _) = Tester.Candidates(idx, cfg, "winws", new TestOptions(Quick: true), presets);
        Assert.Equal("strategy-general", quick![0]);
        Assert.True(quick.Count <= Tester.QuickTop + 2);
        var (q2, _) = Tester.Candidates(idx, cfg, "winws2", new TestOptions(Quick: true), presets);
        Assert.Equal("z2-general", q2![0]);
        Assert.All(q2, id => Assert.StartsWith("z2-", id));
        var (all, _) = Tester.Candidates(idx, cfg, "winws", new TestOptions(), presets);
        Assert.Equal("strategy-alt", all![0]);
        Assert.Equal(64, all.Count);
        var (named, unk) = Tester.Candidates(idx, cfg, "winws", new TestOptions(Strategies: ["strategy-general", "nope"]), presets);
        Assert.Null(named);
        Assert.Equal("nope", unk);
        var (aib, _) = Tester.Candidates(idx, cfg, "winws", new TestOptions(Strategies: ["strategy-general"], ApplyIfBetter: true), presets);
        Assert.Equal(["strategy-alt", "strategy-general"], aib);
        var on = Cfg(new JsonObject { ["enabled"] = true });
        Assert.Equal("forced", Tester.Refusal(on, true, false, true, new TestOptions(Exclusive: true)));
        Assert.Equal("engine_not_running", Tester.Refusal(on, false, false, true, new TestOptions()));
        Assert.Equal("engine_not_running", Tester.Refusal(Cfg(), true, false, true, new TestOptions()));
        Assert.Equal("not_supported", Tester.Refusal(on, true, false, false, new TestOptions()));
        Assert.Null(Tester.Refusal(on, true, false, true, new TestOptions()));
    }
}
