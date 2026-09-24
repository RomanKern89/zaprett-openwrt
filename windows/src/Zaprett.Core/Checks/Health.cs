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
            var r = await engine.EnsureMainAsync(ct).ConfigureAwait(false);
            if (R.IsOk(r) && !R.Bool(r["started"]))
                return R.Ok(new JsonObject { ["action"] = "none" });
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
            ["checked_at"] = m["checked_at"]?.DeepClone(), ["repair_blocked"] = m["repair_blocked"]?.DeepClone(),
        };
    }

    /* ---------- recheck after a conflict is gone ---------- */

    /// <summary>At most one extra check in this many seconds, however often a conflict comes and goes.</summary>
    public const long RecheckInterval = 120;

    /// <summary>Pause before the extra check: the engine may be restarting after the other program went away.</summary>
    public static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>An extra check that could not run (engine not up yet, network awaited, a job) is tried again after this
    /// many seconds instead of waiting for the whole <see cref="RecheckInterval"/>.</summary>
    public const long RecheckRetry = 30;

    /// <summary>Skip reasons that pass by themselves: the extra check stays due.</summary>
    static readonly HashSet<string> TransientSkips = ["not_running", "waiting_network", "job_busy"];

    /// <summary>With the conflict scan failing, a repair is put off for at most this many checks in a row, and only when the
    /// last scan that worked saw a conflict; then the monitor repairs as before (a scanner broken for good must not switch
    /// the automatic selection off).</summary>
    public const int UnknownRepairLimit = 3;

    /// <summary>repair_blocked while a repair is put off because the conflict scan failed.</summary>
    public const string ConflictUnknown = "conflict_unknown";

    enum RecheckOutcome
    {
        Done,
        Transient,
        Failed,
    }

    readonly SemaphoreSlim runGate = new(1, 1);
    readonly Lock conflictGate = new();
    bool? lastConflict;
    bool recheckPending;
    bool recheckRunning;
    long lastRecheck = long.MinValue / 2;
    int unknownStreak;

    /// <summary>The postponement of the extra check was written to the log in this series of passing skips.</summary>
    bool postponedLogged;

    /// <summary>The extra check started last (tests wait for it); null when none was started.</summary>
    public Task? LastRecheck { get; private set; }

    /// <summary>What status, the watchdog and the monitor see of blocking conflicts. When a conflict that was there is gone,
    /// the monitor state may still say "sites stopped" because of it: one extra check runs soon (runCheck = the monitor
    /// check of the dispatcher, its answer), not more often than <see cref="RecheckInterval"/>; a change within that time
    /// waits. A check skipped for a passing reason is tried again after <see cref="RecheckRetry"/>, a failed one after the
    /// whole interval. lifetime: the service's (null before its startup: the change is only remembered); the extra check
    /// is not bound to the call that noticed the change.</summary>
    public void NoteConflict(bool blocking, Func<CancellationToken, Task<JsonObject>> runCheck, CancellationToken? lifetime)
    {
        lock (conflictGate)
        {
            var was = lastConflict ?? R.Bool(Files.ReadJson(MonitorPath, 262144)?["conflict_blocking"]);
            lastConflict = blocking;
            if (blocking)
                recheckPending = false;
            else if (was)
                recheckPending = true;
            if (lifetime is not { } life || !recheckPending || recheckRunning || life.IsCancellationRequested ||
                c.Now - lastRecheck < RecheckInterval)
                return;
            recheckPending = false;
            recheckRunning = true;
            lastRecheck = c.Now;
            // the task's own finally must always run: it is not given the lifetime token (a cancelled one would skip it)
            LastRecheck = Task.Run(() => RecheckAsync(runCheck, life), CancellationToken.None);
        }
    }

    async Task RecheckAsync(Func<CancellationToken, Task<JsonObject>> runCheck, CancellationToken lifetime)
    {
        var outcome = RecheckOutcome.Failed;
        string? skipped = null;
        try
        {
            // the language of the service, not of the window whose status call noticed the change
            T.Use(c.Config.Load().Language);
            await c.P.Clock.Delay(RecheckDelay, lifetime).ConfigureAwait(false);
            var r = await runCheck(lifetime).ConfigureAwait(false);
            skipped = R.Str(r["skipped"]);
            outcome = !R.IsOk(r) ? RecheckOutcome.Failed
                : skipped != null && TransientSkips.Contains(skipped) ? RecheckOutcome.Transient : RecheckOutcome.Done;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            outcome = RecheckOutcome.Done;
        }
        catch (Exception e)
        {
            c.P.Log.Warn(T.S("health.log_recheck_failed", e.Message));
        }
        finally
        {
            var logPostponed = false;
            lock (conflictGate)
            {
                recheckRunning = false;
                if (outcome != RecheckOutcome.Done && lastConflict != true)
                {
                    recheckPending = true;
                    // a skip that passes by itself is retried soon; a failure waits the whole interval (no storm of probes)
                    lastRecheck = outcome == RecheckOutcome.Transient ? c.Now - RecheckInterval + RecheckRetry : c.Now;
                }
                // one line per series of passing skips (retried every 30 s), a new series after a check that ran
                if (outcome == RecheckOutcome.Transient)
                {
                    logPostponed = !postponedLogged;
                    postponedLogged = true;
                }
                else if (outcome == RecheckOutcome.Done)
                {
                    postponedLogged = false;
                }
            }
            if (logPostponed)
                c.P.Log.Info(T.S("health.log_recheck_postponed", T.S("health.postponed." + skipped)));
        }
    }

    string? Skip(ZaprettConfig cfg) =>
        MonitorLogic.SkipReason(cfg, c.UserStopped, engine.Running, c.Jobs.IsBusy || TestBusy(), engine.WaitingNetwork);

    /// <summary>One monitor check (the service calls it every monitor.interval minutes; an extra one runs after a conflict
    /// is gone). Checks never overlap. startRepair starts job "test" with --quick --apply-if-better and returns its answer.</summary>
    public async Task<JsonObject> MonitorRunAsync(ZaprettConfig cfg, Func<JsonObject> startRepair, CancellationToken ct, bool extra = false)
    {
        await runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await MonitorRunUnlockedAsync(cfg, startRepair, extra, ct).ConfigureAwait(false);
        }
        finally
        {
            runGate.Release();
        }
    }

    async Task<JsonObject> MonitorRunUnlockedAsync(ZaprettConfig cfg, Func<JsonObject> startRepair, bool extra, CancellationToken ct)
    {
        var skip = Skip(cfg);
        if (skip != null)
            return R.Ok(new JsonObject { ["skipped"] = skip });
        if (extra)
            c.P.Log.Info(T.S("health.log_recheck"));
        // scanned before and after the probes: a program closed while they ran still spoiled them
        var before = await Conflicts.TryBlockingAsync(c.P, ct).ConfigureAwait(false);
        var (services, _) = PresetLogic.PresetServices(c.LoadPresets(), cfg, null);
        var targets = ProbeRunner.UniqueTargets(services!, cfg.Monitor.MaxTargets);
        var pr = targets.Count > 0
            ? await ProbeRunner.ProbeTargetsAsync(c.P.Http, targets, cfg.Monitor.MaxTargets, cfg.Monitor.Timeout, null, ct).ConfigureAwait(false)
            : new ProbeSummary(0, 0, null, []);
        // a job or a stop in the middle of the check would give a false failure
        skip = Skip(cfg);
        if (skip != null)
            return R.Ok(new JsonObject { ["skipped"] = skip });
        // another bypass program breaks the traffic whatever the strategy: its failures are not the strategy's
        var after = await Conflicts.TryBlockingAsync(c.P, ct).ConfigureAwait(false);
        var blocking = after is { Count: > 0 } ? after : before ?? [];
        var conflict = blocking.Count > 0;
        // a failed scan: whether another program interferes is unknown
        var unknown = !conflict && (before == null || after == null);
        var now = c.Now;
        var prev = Files.ReadJson(MonitorPath, 262144);
        bool seenConflict;
        int streak;
        lock (conflictGate)
        {
            seenConflict = lastConflict ?? R.Bool(prev?["conflict_blocking"]);
            unknownStreak = unknown ? unknownStreak + 1 : 0;
            streak = unknownStreak;
            if (after != null)
            {
                lastConflict = after.Count > 0;
                // gone during the probes: they were spoiled, one more check is due
                recheckPending = conflict && after.Count == 0;
            }
        }
        var (st, repair) = MonitorLogic.Step(prev, pr.Ok, pr.Total, now, cfg.Monitor, conflict);
        st["conflict_blocking"] = after != null ? after.Count > 0 : conflict || (unknown && seenConflict);
        // unknown, but the last scan that worked saw a conflict: the repair waits a few checks, not for ever
        var postponed = repair && unknown && seenConflict && streak <= UnknownRepairLimit;
        if (postponed)
        {
            repair = false;
            c.P.Log.Warn(T.S("health.log_repair_unknown"));
        }
        var prevState = R.Str(prev?["state"]);
        if (R.Str(st["state"]) == "degraded" && prevState != "degraded" && prevState != "repairing")
            c.P.Log.Warn(T.S("health.log_degraded", pr.Ok, pr.Total, st["consecutive_failures"]?.ToJsonString()));
        // kept in the state until the next check: a failed check while another program blocks the traffic
        var blocked = conflict && pr.Total > 0 && pr.Ok * 2 < pr.Total;
        st["repair_blocked"] = blocked ? Conflicts.Warning : postponed ? ConflictUnknown : null;
        if (blocked)
            c.P.Log.Warn(T.S("health.log_repair_conflict", string.Join(", ", blocking.Select(b => R.Str(b?["name"])))));
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
            ["repair_blocked"] = blocked ? Conflicts.Warning : postponed ? ConflictUnknown : null,
            ["conflicts_blocking"] = conflict ? blocking : null,
        });
    }
}
