using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Util;

namespace Zaprett.Core.Checks;

/// <summary>Pure logic of the availability monitor and the watchdog (router health.uc, contract v1.3 §14).</summary>
public static class MonitorLogic
{
    public const int History = 48;
    /// <summary>The monitor starts an automatic selection at most once in this many seconds.</summary>
    public const long RepairInterval = 6 * 3600;

    public static readonly IReadOnlyList<string> SkipReasons = ["monitor_disabled", "service_disabled", "stopped", "not_running", "waiting_network", "job_busy"];

    public static JsonObject Empty() => new()
    {
        ["state"] = "unknown", ["checked_at"] = null, ["ok"] = 0, ["total"] = 0, ["consecutive_failures"] = 0,
        ["history"] = new JsonArray(), ["last_repair"] = null, ["repair_blocked"] = null, ["conflict_blocking"] = false,
    };

    /// <summary>The state after one check. A check fails when fewer than half of the targets answered (ok * 2 &lt; total);
    /// total 0 does not count (state unknown). repair: degraded, auto_repair on and no repair within 6 hours. conflict:
    /// another bypass program blocks the traffic now; a failure then has a known cause that is not the strategy, so it
    /// is recorded in the history but does not count towards degraded and never starts a repair.</summary>
    public static (JsonObject State, bool Repair) Step(JsonObject? prev, int ok, int total, long now, MonitorConfig m,
        bool conflict = false)
    {
        var st = Empty();
        if (prev != null)
            foreach (var k in st.Select(kv => kv.Key).ToList())
                if (prev[k] != null)
                    st[k] = prev[k]!.DeepClone();
        var history = prev?["history"] is JsonArray h ? (JsonArray)h.DeepClone() : [];
        st["history"] = history;
        st["checked_at"] = now;
        st["ok"] = ok;
        st["total"] = total;
        if (total == 0)
        {
            st["state"] = "unknown";
            st["consecutive_failures"] = 0;
            return (st, false);
        }
        var failed = ok * 2 < total;
        var entry = new JsonObject { ["t"] = now, ["ok"] = ok, ["total"] = total };
        if (conflict)
            entry["conflict"] = true;
        history.Add(entry);
        while (history.Count > History)
            history.RemoveAt(0);
        var prevFails = R.Long(st["consecutive_failures"]) ?? 0;
        var fails = !failed ? 0 : conflict ? prevFails : prevFails + 1;
        st["consecutive_failures"] = fails;
        st["state"] = fails >= m.Threshold ? "degraded" : "ok";
        var last = R.Long(st["last_repair"]?["t"]);
        var repair = !conflict && R.Str(st["state"]) == "degraded" && m.AutoRepair && (last == null || now - last >= RepairInterval);
        return (st, repair);
    }

    /// <summary>Why the monitor does not check now, or null. An engine waiting for its network bypasses nothing on purpose:
    /// probing it would count failures and start a repair that replaces a working strategy.</summary>
    public static string? SkipReason(ZaprettConfig cfg, bool stopped, bool running, bool busy, bool waitingNetwork = false)
    {
        if (!cfg.Monitor.Enabled)
            return "monitor_disabled";
        if (!cfg.Enabled)
            return "service_disabled";
        if (stopped)
            return "stopped";
        if (!running)
            return "not_running";
        if (waitingNetwork)
            return "waiting_network";
        return busy ? "job_busy" : null;
    }

    /// <summary>What the watchdog must do (router ensure): none / skipped / start the engine / restore the firewall rule.</summary>
    public static (string Action, string? Reason) EnsureDecision(ZaprettConfig cfg, bool stopped, bool testBusy, bool running,
        bool firewallOk)
    {
        if (!cfg.Enabled)
            return ("none", "disabled");
        if (stopped)
            return ("none", "stopped");
        if (testBusy)
            return ("skipped", "test_running");
        if (!running)
            return ("started", "not_running");
        return firewallOk ? ("none", null) : ("fw_applied", null);
    }
}
