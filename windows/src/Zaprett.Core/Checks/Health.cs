using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Engine;
using Zaprett.Core.Jobs;
using Zaprett.Core.Presets;
using Zaprett.Core.Util;
using Zaprett.Core.Text;

namespace Zaprett.Core.Checks;

/// <summary>Watchdog ("ensure"), live check of the preset services ("probe") and the availability monitor with
/// automatic repair (router health.uc, contract v1.3 §14). None of them stops or restarts a working engine.</summary>
public sealed class Health
{
    readonly CoreContext c;
    readonly EngineService engine;

    public Health(CoreContext context, EngineService engine)
    {
        c = context;
        this.engine = engine;
    }

    public string ProbePath => c.RunFile("probe.json");

    public string MonitorPath => c.RunFile("monitor.json");

    /// <summary>An automatic selection is running (its state file exists or its job is alive).</summary>
    public bool TestBusy() => engine.TestActive || TestBusyByJob();

    /// <summary>A job "test" is running now.</summary>
    public bool TestBusyByJob()
    {
        var j = c.Jobs.Read();
        return j != null && R.Str(j["name"]) == "test" && R.Str(j["state"]) == "running";
    }

    /* ---------- ensure ---------- */

    public async Task<JsonObject> EnsureAsync(ZaprettConfig cfg, CancellationToken ct)
    {
        var running = engine.Running;
        var fwOk = !running || !cfg.QuicBlock || await c.P.Firewall.IsQuicBlockedAsync(ct).ConfigureAwait(false);
        var (action, reason) = MonitorLogic.EnsureDecision(cfg, c.UserStopped, TestBusy(), running, fwOk);
        if (action == "started")
        {
            var r = await engine.StartMainAsync(cfg, ct).ConfigureAwait(false);
            if (!R.IsOk(r))
            {
                c.P.Log.Error(T.S("health.log_ensure_failed", R.Message(r)));
                return R.Fail("engine_not_running", T.S("health.ensure_failed"),
                    new JsonObject { ["action"] = "started", ["reason"] = "not_running", ["cause"] = R.Error(r) });
            }
            c.P.Log.Warn(T.S("health.log_ensure_started", r["pid"]?.ToJsonString() ?? "?"));
            return R.Ok(new JsonObject { ["action"] = "started", ["reason"] = "not_running", ["pid"] = r["pid"]?.DeepClone() });
        }
        if (action == "fw_applied")
        {
            await engine.ApplyFirewallAsync(cfg, true, ct).ConfigureAwait(false);
            c.P.Log.Warn(T.S("health.log_fw_applied"));
            return R.Ok(new JsonObject { ["action"] = "fw_applied" });
        }
        var res = R.Ok(new JsonObject { ["action"] = action });
        if (reason != null)
            res["reason"] = reason;
        return res;
    }

    /* ---------- probe ---------- */

    public async Task<JsonObject> ProbeRunAsync(ZaprettConfig cfg, IReadOnlyList<string>? ids, JobContext ctx)
    {
        var (services, unknown) = PresetLogic.PresetServices(c.LoadPresets(), cfg, ids);
        if (services == null)
            return R.Fail("unknown_service", T.S("health.unknown_service", unknown));
        var targets = ProbeRunner.UniqueTargets(services, null);
        var res = new JsonObject
        {
            ["started"] = c.Now, ["finished"] = 0, ["engine_running"] = engine.Running,
            ["strategy"] = cfg.CurrentStrategy() is { Length: > 0 } s ? s : null, ["ok"] = 0, ["total"] = 0, ["services"] = new JsonArray(),
        };
        ctx.Progress(5, T.S("health.progress_probe", services.Count, targets.Count));
        var pr = targets.Count > 0
            ? await ProbeRunner.ProbeTargetsAsync(c.P.Http, targets, cfg.Test.Concurrency, cfg.Test.Timeout, null, ctx.Token).ConfigureAwait(false)
            : new ProbeSummary(0, 0, null, []);
        var (ok, total, per) = ProbeRunner.PerService(services, pr);
        res["ok"] = ok;
        res["total"] = total;
        res["services"] = per;
        res["finished"] = c.Now;
        if (!Files.WriteJson(ProbePath, res))
            return R.Fail("write_failed", T.S("health.write_failed", ProbePath));
        foreach (var sv in per.OfType<JsonObject>())
            ctx.Log(T.S("health.log_service", R.Str(sv["id"]), sv["ok"]?.ToJsonString(), sv["total"]?.ToJsonString()));
        return R.Ok(new JsonObject
        {
            ["reachable"] = ok, ["total"] = total, ["services"] = per.Count, ["message"] = T.S("health.probe_result", ok, total),
        });
    }

    public JsonObject ProbeStatus() => R.Ok(new JsonObject { ["probe"] = Files.ReadJson(ProbePath) });

    /* ---------- monitor ---------- */

    public JsonObject MonitorStatus(ZaprettConfig cfg)
    {
        var m = Files.ReadJson(MonitorPath, 262144) ?? MonitorLogic.Empty();
        var e = MonitorLogic.Empty();
        foreach (var k in e.Select(kv => kv.Key).ToList())
            if (m[k] != null)
                e[k] = m[k]!.DeepClone();
        // a repair job that ended (or died) no longer keeps the state at "repairing"
        if (R.Str(e["state"]) == "repairing")
        {
            var j = c.Jobs.Read();
            if (j == null || R.Str(j["id"]) != R.Str(e["last_repair"]?["job_id"]) || R.Str(j["state"]) != "running")
                e["state"] = (R.Long(e["consecutive_failures"]) ?? 0) >= cfg.Monitor.Threshold ? "degraded" : "ok";
        }
        e["enabled"] = cfg.Monitor.Enabled;
        e["auto_repair"] = cfg.Monitor.AutoRepair;
        e["interval"] = cfg.Monitor.Interval;
        e["threshold"] = cfg.Monitor.Threshold;
        return R.Ok(new JsonObject { ["monitor"] = e });
    }

    /// <summary>Compact state for "status": null when the monitor is off.</summary>
    public JsonObject? MonitorBrief(ZaprettConfig cfg)
    {
        if (!cfg.Monitor.Present || !cfg.Monitor.Enabled)
            return null;
        var m = (JsonObject)MonitorStatus(cfg)["monitor"]!;
        return new JsonObject
        {
            ["state"] = m["state"]?.DeepClone(), ["consecutive_failures"] = m["consecutive_failures"]?.DeepClone(),
            ["checked_at"] = m["checked_at"]?.DeepClone(),
        };
    }

    string? Skip(ZaprettConfig cfg) =>
        MonitorLogic.SkipReason(cfg, c.UserStopped, engine.Running, c.Jobs.IsBusy || TestBusy());

    /// <summary>One monitor check (the service calls it every monitor.interval minutes). startRepair starts job "test"
    /// with --quick --apply-if-better and returns its answer.</summary>
    public async Task<JsonObject> MonitorRunAsync(ZaprettConfig cfg, Func<JsonObject> startRepair, CancellationToken ct)
    {
        var skip = Skip(cfg);
        if (skip != null)
            return R.Ok(new JsonObject { ["skipped"] = skip });
        var (services, _) = PresetLogic.PresetServices(c.LoadPresets(), cfg, null);
        var targets = ProbeRunner.UniqueTargets(services!, cfg.Monitor.MaxTargets);
        var pr = targets.Count > 0
            ? await ProbeRunner.ProbeTargetsAsync(c.P.Http, targets, cfg.Monitor.MaxTargets, cfg.Monitor.Timeout, null, ct).ConfigureAwait(false)
            : new ProbeSummary(0, 0, null, []);
        // a job or a stop in the middle of the check would give a false failure
        skip = Skip(cfg);
        if (skip != null)
            return R.Ok(new JsonObject { ["skipped"] = skip });
        var now = c.Now;
        var prev = Files.ReadJson(MonitorPath, 262144);
        var (st, repair) = MonitorLogic.Step(prev, pr.Ok, pr.Total, now, cfg.Monitor);
        var prevState = R.Str(prev?["state"]);
        if (R.Str(st["state"]) == "degraded" && prevState != "degraded" && prevState != "repairing")
            c.P.Log.Warn(T.S("health.log_degraded", pr.Ok, pr.Total, st["consecutive_failures"]?.ToJsonString()));
        if (repair)
        {
            var j = startRepair();
            if (R.IsOk(j))
            {
                st["state"] = "repairing";
                st["last_repair"] = new JsonObject { ["t"] = now, ["job_id"] = j["job"]?["id"]?.DeepClone() };
                c.P.Log.Warn(T.S("health.log_repair_started", R.Str(j["job"]?["id"])));
            }
            else
            {
                c.P.Log.Error(T.S("health.log_repair_failed", R.Message(j) ?? R.Error(j)));
            }
        }
        if (!Files.WriteJson(MonitorPath, st))
            return R.Fail("write_failed", T.S("health.write_failed", MonitorPath));
        return R.Ok(new JsonObject
        {
            ["state"] = st["state"]?.DeepClone(), ["reachable"] = pr.Ok, ["total"] = pr.Total,
            ["consecutive_failures"] = st["consecutive_failures"]?.DeepClone(), ["repair_started"] = R.Str(st["state"]) == "repairing",
        });
    }
}
