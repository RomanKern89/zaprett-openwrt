using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Engine;
using Zaprett.Core.Jobs;
using Zaprett.Core.Presets;
using Zaprett.Core.Store;
using Zaprett.Core.Strategy;
using Zaprett.Core.Util;
using Zaprett.Core.Text;

namespace Zaprett.Core.Checks;

public sealed record TestOptions(bool Quick = false, IReadOnlyList<string>? Strategies = null, bool ApplyIfBetter = false, bool Exclusive = false);

/// <summary>Automatic strategy selection (router tester.uc, contract §10 and v1.6 §17). The configured strategy is never
/// changed while testing. Mode isolated: the main instance keeps working and the candidates run as instance "test"
/// for the check connections only (local ports <see cref="CoreOptions.TestLocalPortFrom"/>..To; the service applies the
/// isolation filter to instance "test" and excludes the range from "main"). Mode exclusive: the main instance itself
/// runs the candidates, and the configured strategy is started again at the end. The core decides; the service
/// implements the isolation.</summary>
public sealed class Tester
{
    public const int QuickTop = 10;
    public const int ListReadLimit = 1048576;
    public const int TargetsResults = 10;
    public const int TargetsMax = 100;

    public static readonly IReadOnlyList<string> ModeReasons =
    [
        "forced", "engine_not_running", "not_supported", "instance_failed", "write_failed",
    ];

    readonly CoreContext c;
    readonly EngineService engine;

    public Tester(CoreContext context, EngineService engine)
    {
        c = context;
        this.engine = engine;
    }

    public string ResultsPath => c.RunFile("test-results.json");

    /* ---------- pure parts ---------- */

    public static double Ratio(int ok, int total) => total > 0 ? ok * 1.0 / total : 0;

    /// <summary>ratio desc, then avg_ms asc, then id; tested results before the others.</summary>
    public static List<JsonObject> SortResults(IEnumerable<JsonObject> results) =>
        results.OrderBy(r => R.Str(r["status"]) == "done" ? 0 : 1)
            .ThenByDescending(r => (double)(R.Number(r["ratio"]) ?? 0))
            .ThenBy(r => R.Long(r["avg_ms"]) ?? 1000000000)
            .ThenBy(r => R.Str(r["id"]), StringComparer.Ordinal)
            .ToList();

    /// <summary>The best tested strategy when its share of reachable targets is strictly higher than the original's
    /// (an original that did not run counts as 0); null otherwise.</summary>
    public static string? PickBetter(IReadOnlyList<JsonObject> results, string? original)
    {
        var best = results.FirstOrDefault(r => R.Str(r["status"]) == "done" && (R.Long(r["ok"]) ?? 0) > 0);
        if (best == null || R.Str(best["id"]) == original)
            return null;
        var orig = results.FirstOrDefault(r => R.Str(r["id"]) == original);
        var baseRatio = orig != null && R.Str(orig["status"]) == "done" ? R.Number(orig["ratio"]) ?? 0 : 0;
        return (R.Number(best["ratio"]) ?? 0) > baseRatio ? R.Str(best["id"]) : null;
    }

    /// <summary>Candidates: the named ones; --quick: the defaults of the presets (winws2: *_nfqws2) and the first bundle
    /// strategies; otherwise the current one and all installed. With apply_if_better the current one always runs first.</summary>
    public static (List<string>? Ids, string? UnknownId) Candidates(StoreIndex idx, ZaprettConfig cfg, string eng, TestOptions o, JsonObject? presets)
    {
        var avail = idx.Items[Engines.ItemType(eng)];
        var ids = new List<string>();
        void Add(string? id)
        {
            if (id != null && avail.ContainsKey(id) && !ids.Contains(id))
                ids.Add(id);
        }
        if (o.Strategies is { Count: > 0 })
        {
            foreach (var id in o.Strategies)
            {
                if (!avail.ContainsKey(id))
                    return (null, id);
                Add(id);
            }
        }
        else if (o.Quick)
        {
            var d = presets?["defaults"];
            var suffix = eng == Engines.Winws2 ? "_nfqws2" : "";
            Add(R.Str(d?["strategy" + suffix]));
            foreach (var id in R.Strings(d?["quick_test_strategies" + suffix]))
                if (ids.Count < QuickTop + 2)
                    Add(id);
            foreach (var id in avail.Values.Where(v => v.Source == "bundle").Select(v => v.Id).Order(StringComparer.Ordinal))
                if (ids.Count < QuickTop + 1)
                    Add(id);
        }
        else
        {
            Add(cfg.CurrentStrategy(eng));
            foreach (var id in avail.Keys.Order(StringComparer.Ordinal))
                Add(id);
        }
        var cur = cfg.CurrentStrategy(eng);
        if (o.ApplyIfBetter && cur.Length > 0 && avail.ContainsKey(cur) && !ids.Contains(cur))
            ids.Insert(0, cur);
        return (ids, null);
    }

    /// <summary>Why mode isolated cannot be used, or null.</summary>
    public static string? Refusal(ZaprettConfig cfg, bool running, bool stopped, bool supported, TestOptions o)
    {
        if (o.Exclusive)
            return "forced";
        if (!cfg.Enabled || !running || stopped)
            return "engine_not_running";
        return supported ? null : "not_supported";
    }

    /// <summary>Results trimmed for the IPC message limit: targets only for the best results.</summary>
    public static JsonObject? Trim(JsonObject? res)
    {
        if (res?["results"] is not JsonArray arr)
            return res;
        var trimmed = false;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject r || r["targets"] is not JsonArray t)
                continue;
            if (i >= TargetsResults)
            {
                r.Remove("targets");
                trimmed = true;
            }
            else if (t.Count > TargetsMax)
            {
                while (t.Count > TargetsMax)
                    t.RemoveAt(t.Count - 1);
                trimmed = true;
            }
        }
        if (res["baseline"]?["targets"] is JsonArray bt && bt.Count > TargetsMax)
        {
            while (bt.Count > TargetsMax)
                bt.RemoveAt(bt.Count - 1);
            trimmed = true;
        }
        res["targets_trimmed"] = trimmed;
        return res;
    }

    /// <summary>Results without per-target details ("test.status" with brief).</summary>
    public static JsonObject? Brief(JsonObject? res)
    {
        if (res == null)
            return null;
        foreach (var r in (res["results"] as JsonArray ?? []).OfType<JsonObject>())
            r.Remove("targets");
        (res["baseline"] as JsonObject)?.Remove("targets");
        return res;
    }

    /* ---------- run ---------- */

    List<string> ReadListTexts(StoreIndex idx, ZaprettConfig cfg)
    {
        var texts = new List<string>();
        foreach (var id in cfg.Lists)
        {
            var it = idx.Get("list", id);
            if (it == null || !File.Exists(it.File) || ItemStore.IsGzip(it.File))
                continue;
            texts.Add(ReadHead(it.File));
        }
        return texts;
    }

    /// <summary>At most the first ListReadLimit bytes of a list, cut at the last whole line (lists may be 16 MiB).</summary>
    static string ReadHead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[ListReadLimit];
            var n = 0;
            int r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0)
                n += r;
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            if (fs.Length > n)
            {
                var nl = text.LastIndexOf('\n');
                text = nl >= 0 ? text[..nl] : "";
            }
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    (int, int)? Ports(bool isolated) => isolated ? (c.Options.TestLocalPortFrom, c.Options.TestLocalPortTo) : null;

    public async Task<JsonObject> RunAsync(ZaprettConfig cfg, TestOptions o, JobContext ctx)
    {
        var eng = cfg.Engine;
        if (!File.Exists(Engines.Executable(c.Paths, eng)))
            return R.Fail("engine_missing", T.S("svc.engine_missing", eng));
        var idx = c.Store.Scan();
        var presets = c.LoadPresets();
        var (ids, unknown) = Candidates(idx, cfg, eng, o, presets);
        if (ids == null)
            return R.Fail("strategy_not_found", T.S("strategy.not_found_engine", unknown, eng));
        if (ids.Count == 0)
            return R.Fail("no_strategies", T.S("test.no_strategies"));
        var targets = PresetLogic.BuildTargets(presets, cfg, ReadListTexts(idx, cfg), cfg.Test.MaxDomains);
        if (targets.Count == 0)
            return R.Fail("no_targets", T.S("test.no_targets"));

        var running = engine.Running;
        var original = cfg.CurrentStrategy(eng);
        var reason = Refusal(cfg, running, c.UserStopped, c.Options.IsolationSupported, o);
        var state = new JsonObject
        {
            ["started"] = c.Now, ["was_running"] = running, ["engine"] = eng, ["strategy"] = original,
            ["mode"] = reason == null ? "isolated" : "exclusive", ["mode_reason"] = reason, ["applied"] = null,
        };
        if (!Files.WriteJson(engine.TestStatePath, state))
            return R.Fail("write_failed", T.S("test.write_failed", engine.TestStatePath));
        var results = new JsonObject
        {
            ["started"] = c.Now, ["finished"] = 0, ["state"] = "running", ["engine"] = eng, ["mode"] = state["mode"]!.DeepClone(),
            ["mode_reason"] = reason, ["original_strategy"] = original, ["targets"] = R.Arr(targets.Select(t => t.Url)),
            ["baseline"] = null, ["results"] = new JsonArray(),
        };
        Files.WriteJson(ResultsPath, results);
        c.P.Log.Info(reason != null ? T.S("test.log_start_reason", ids.Count, targets.Count, R.Str(state["mode"]), reason)
            : T.S("test.log_start", ids.Count, targets.Count, R.Str(state["mode"])));

        var list = new List<JsonObject>();
        Exception? exc = null;
        try
        {
            var isolated = R.Str(state["mode"]) == "isolated";
            if (isolated)
            {
                ctx.Progress(1, T.S("test.progress_prepare"));
                var why = await IsolateSetupAsync(cfg, eng, targets, ctx.Token).ConfigureAwait(false);
                if (why != null)
                {
                    isolated = false;
                    state["mode"] = "exclusive";
                    state["mode_reason"] = why;
                    Files.WriteJson(engine.TestStatePath, state);
                    results["mode"] = "exclusive";
                    results["mode_reason"] = why;
                    Files.WriteJson(ResultsPath, results);
                    c.P.Log.Warn(T.S("test.log_no_isolation", why));
                    ctx.Log(T.S("test.job_no_isolation", why));
                }
            }

            ctx.Progress(2, T.S("test.progress_baseline", targets.Count));
            if (!isolated)
                await engine.StopMainAsync(ctx.Token).ConfigureAwait(false);
            var bl = await ProbeRunner.ProbeTargetsAsync(c.P.Http, targets, cfg.Test.Concurrency, cfg.Test.Timeout, Ports(isolated), ctx.Token)
                .ConfigureAwait(false);
            var baseline = ProbeRunner.SummaryJson(bl);
            baseline["note"] = bl.Ok == bl.Total ? "no_blocking_detected" : null;
            results["baseline"] = baseline;
            Files.WriteJson(ResultsPath, results);
            ctx.Log(T.S("test.log_baseline", bl.Ok, bl.Total));

            for (var i = 0; i < ids.Count; i++)
            {
                if (ctx.Cancelled)
                    break;
                var sid = ids[i];
                var item = idx.Get(Engines.ItemType(eng), sid)!;
                ctx.Progress(5 + 90 * i / ids.Count, T.S("test.progress_strategy", i + 1, ids.Count, sid));
                var r = await RunCandidateAsync(cfg, eng, sid, item, idx, targets, isolated, ctx.Token).ConfigureAwait(false);
                ctx.Log(T.S("test.log_candidate", sid, R.Str(r["status"]), r["ok"]?.ToJsonString(), r["total"]?.ToJsonString()));
                list.Add(r);
                list = SortResults(list);
                results["results"] = R.Arr(list.Select(x => (JsonNode?)x));
                Files.WriteJson(ResultsPath, results);
                ctx.Partial(new JsonObject { ["tested"] = list.Count, ["total"] = ids.Count, ["mode"] = state["mode"]!.DeepClone() });
            }
        }
        catch (OperationCanceledException) when (ctx.Cancelled)
        {
        }
        catch (Exception e)
        {
            exc = e;
        }

        string? applied = null;
        try
        {
            if (o.ApplyIfBetter && exc == null && !ctx.Cancelled)
            {
                var win = PickBetter(list, original);
                if (win != null && c.Config.Set("main", new JsonObject { [Engines.StrategyOption(eng)] = win }))
                {
                    applied = win;
                    c.P.Log.Info(T.S("test.log_applied", win, original.Length > 0 ? original : "—"));
                    ctx.Log(T.S("test.job_applied", win));
                }
            }
            results["applied"] = applied;
            state["applied"] = applied;
            ctx.Progress(97, applied != null ? T.S("test.progress_restart_new") :
                R.Str(state["mode"]) == "isolated" ? T.S("test.progress_remove_instance") : T.S("test.progress_restore"));
        }
        finally
        {
            // the engine is returned whatever happened above
            await RestoreAsync(state).ConfigureAwait(false);
        }
        results["finished"] = c.Now;
        results["state"] = exc != null ? "failed" : ctx.Cancelled ? "cancelled" : "done";
        Files.WriteJson(ResultsPath, results);
        if (exc != null)
            return R.Fail("internal_error", T.S("test.failed", exc.Message));
        var best = list.FirstOrDefault(r => R.Str(r["status"]) == "done" && (R.Long(r["ok"]) ?? 0) > 0);
        var msg = best == null ? T.S("test.none_better")
            : applied != null ? T.S("test.best_applied", R.Str(best["id"]), original.Length > 0 ? original : "—")
            : T.S("test.best", R.Str(best["id"]));
        return R.Ok(new JsonObject
        {
            ["tested"] = list.Count, ["best"] = best?["id"]?.DeepClone(), ["baseline_ok"] = results["baseline"]?["ok"]?.DeepClone(),
            ["applied"] = applied, ["mode"] = state["mode"]!.DeepClone(), ["mode_reason"] = state["mode_reason"]?.DeepClone(), ["message"] = msg,
        });
    }

    /// <summary>The second instance must be able to run at all: it starts once with the strategy the main one runs now
    /// and is stopped again before the baseline. Returns null or the fallback reason.</summary>
    /// <summary>Starts instance "test" with a pass-through strategy and KEEPS it running until the restore. The service
    /// isolates the check traffic only while "test" runs (S-5: the main instance is restarted with the exclusion filter
    /// when "test" starts and back when it stops), so stopping it here would (a) restart the main engine twice more and
    /// (b) let the main engine desync the baseline checks — the baseline would not be "without the bypass". The
    /// pass-through strategy matches only the guard host name that never occurs, so the baseline sees no desync at all.
    /// Returns null or the fallback reason.</summary>
    async Task<string?> IsolateSetupAsync(ZaprettConfig cfg, string eng, IReadOnlyList<Target> targets, CancellationToken ct)
    {
        var g = await engine.GenerateAsync(cfg, PassThrough(eng, targets), ct).ConfigureAwait(false);
        if (!g.Ok)
            return "instance_failed";
        c.Generator.EnsureFiles(cfg, g.Args);
        var st = await c.P.Engine.StartAsync(EngineService.TestInstance, Engines.Executable(c.Paths, eng), g.Args, ct).ConfigureAwait(false);
        if (st.Running)
            await c.P.Clock.Delay(c.Options.EngineCheckDelay, ct).ConfigureAwait(false);
        if (st.Running && c.P.Engine.GetState(EngineService.TestInstance).Running)
            return null;
        await c.P.Engine.StopAsync(EngineService.TestInstance, ct).ConfigureAwait(false);
        return "instance_failed";
    }

    /// <summary>Strategy of instance "test" for the baseline: the TCP ports of the targets, matched only by the guard host
    /// name (zaprett-guard.invalid), so nothing is ever desynced.</summary>
    GenerateOptions PassThrough(string eng, IReadOnlyList<Target> targets)
    {
        var ports = targets.Select(t => Diagnose.UrlHost(t.Url)?.Port).OfType<int>().Distinct().Order().ToList();
        if (ports.Count == 0)
            ports = [443];
        var text = $"--filter-tcp={string.Join(',', ports)} --hostlist={Guard.GuardFiles.Hostlist(c.Paths)}";
        var item = new StoreItem("pass-through", Engines.ItemType(eng), "pass-through", "", "", "", null, null, [], "", "user", null, null,
            null, null, null);
        return new GenerateOptions { Engine = eng, Text = text, Item = item, TestMode = true };
    }

    async Task<JsonObject> RunCandidateAsync(ZaprettConfig cfg, string eng, string sid, StoreItem item, StoreIndex idx,
        IReadOnlyList<Target> targets, bool isolated, CancellationToken ct)
    {
        var r = new JsonObject
        {
            ["id"] = sid, ["name"] = item.Name, ["ok"] = 0, ["total"] = targets.Count, ["ratio"] = 0.0, ["avg_ms"] = null,
            ["status"] = null, ["message"] = null, ["targets"] = new JsonArray(),
        };
        var g = await engine.GenerateAsync(cfg, new GenerateOptions { Engine = eng, Strategy = sid, Index = idx, TestMode = true }, ct)
            .ConfigureAwait(false);
        if (!g.Ok)
        {
            r["status"] = "invalid";
            r["message"] = g.Message;
            return r;
        }
        var (fail, _) = await engine.CheckAsync(g, cfg, ct).ConfigureAwait(false);
        if (fail != null)
        {
            r["status"] = "invalid";
            r["message"] = R.Message(fail);
            return r;
        }
        var instance = isolated ? EngineService.TestInstance : EngineService.Main;
        var st = isolated
            ? await c.P.Engine.StartAsync(instance, Engines.Executable(c.Paths, eng), g.Args, ct).ConfigureAwait(false)
            : await engine.RunCandidateInMainAsync(g, ct).ConfigureAwait(false);
        if (cfg.Test.Settle > 0)
            await c.P.Clock.Delay(TimeSpan.FromSeconds(cfg.Test.Settle), ct).ConfigureAwait(false);
        if (!st.Running || !c.P.Engine.GetState(instance).Running)
        {
            r["status"] = "engine_failed";
            r["message"] = T.S("test.engine_failed");
            return r;
        }
        var pr = await ProbeRunner.ProbeTargetsAsync(c.P.Http, targets, cfg.Test.Concurrency, cfg.Test.Timeout, Ports(isolated), ct)
            .ConfigureAwait(false);
        r["ok"] = pr.Ok;
        r["ratio"] = Ratio(pr.Ok, pr.Total);
        r["avg_ms"] = pr.AvgMs;
        r["targets"] = R.Arr(pr.Targets.Select(t => (JsonNode?)t.ToJson(true)));
        r["status"] = "done";
        return r;
    }

    /// <summary>Returns the service to the state saved before the test (also for a test the service lost: state file
    /// without a running job). Isolated: instance "test" is stopped; the main one is restarted only for an applied
    /// strategy, or started when it died meanwhile. Exclusive: the configured strategy is started again when the engine
    /// was running, otherwise it stays stopped. Never started when the user stopped the service.</summary>
    public async Task<JsonObject?> RestoreAsync(JsonObject? state = null)
    {
        // one restore at a time (status of several clients, the job itself): a second caller finds nothing to do
        if (!restoreGate.Wait(0))
            return null;
        try
        {
            return await RestoreUnlockedAsync(state).ConfigureAwait(false);
        }
        finally
        {
            restoreGate.Release();
        }
    }

    readonly SemaphoreSlim restoreGate = new(1, 1);

    async Task<JsonObject?> RestoreUnlockedAsync(JsonObject? state)
    {
        state ??= Files.ReadJson(engine.TestStatePath, 65536);
        if (state == null && !File.Exists(engine.TestStatePath))
            return null;
        var ct = CancellationToken.None;
        JsonObject? r = null;
        try
        {
            await c.P.Engine.StopAsync(EngineService.TestInstance, ct).ConfigureAwait(false);
            var cfg = c.Config.Load();
            var wasRunning = R.Bool(state?["was_running"]);
            var applied = R.Str(state?["applied"]) != null;
            // the user may have disabled or stopped the service during the selection
            var allowed = cfg.Enabled && !c.UserStopped;
            if (!allowed)
                await engine.StopMainAsync(ct).ConfigureAwait(false);
            else if (R.Str(state?["mode"]) == "isolated")
            {
                if (applied || (wasRunning && !engine.Running))
                    r = await engine.StartMainAsync(cfg, ct).ConfigureAwait(false);
            }
            else if (wasRunning)
                r = await engine.StartMainAsync(cfg, ct).ConfigureAwait(false);
            else
                await engine.StopMainAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // the state goes anyway: with it the watchdog would never start the engine again; the watchdog now can
            c.P.Log.Error(T.S("test.log_restore_failed", e.Message));
            r = R.Fail("restore_failed", T.S("test.restore_failed", e.Message));
        }
        Files.TryDelete(engine.TestStatePath);
        var res = Files.ReadJson(ResultsPath, 4194304);
        if (res != null && R.Str(res["state"]) == "running")
        {
            res["state"] = "failed";
            res["finished"] = c.Now;
            Files.WriteJson(ResultsPath, res);
        }
        return r ?? R.Ok();
    }

    public JsonObject? ReadResults() => Files.ReadJson(ResultsPath, 4194304);
}
