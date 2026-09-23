using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Platform;
using Zaprett.Core.Strategy;
using Zaprett.Core.Util;
using Zaprett.Core.Text;

namespace Zaprett.Core.Engine;

/// <summary>The engine as the commands see it: generate → check (dry-run) → start instance "main" through
/// <see cref="Platform.IEngineControl"/>, the QUIC rule of the firewall, and the generation status for "status"
/// (router service.uc + init script).</summary>
public sealed class EngineService
{
    public const string Main = "main";
    public const string TestInstance = "test";

    readonly CoreContext c;
    readonly SemaphoreSlim gate = new(1, 1);
    List<string>? lastArgs;
    string? lastEngine;

    public EngineService(CoreContext context)
    {
        c = context;
    }

    public string TestStatePath => c.RunFile("test-state.json");

    /// <summary>An automatic selection is running or left its state behind.</summary>
    public bool TestActive => File.Exists(TestStatePath);

    public bool Running => c.P.Engine.GetState(Main).Running;

    public string StatusPath => c.RunFile("status.json");

    public async Task<GenerateResult> GenerateAsync(ZaprettConfig cfg, GenerateOptions? opts, CancellationToken ct)
    {
        opts ??= new GenerateOptions();
        if (cfg.NetworkFilter.SkipCorporate && opts.NlmNetworks == null && c.Options.NetworkList != null)
        {
            var nets = await c.Options.NetworkList.GetNonCorporateNetworksAsync(ct).ConfigureAwait(false);
            opts = opts with { NlmNetworks = nets };
        }
        return c.Generator.Build(cfg, opts);
    }

    /// <summary>Dry-run of the engine; null when the arguments passed, otherwise the failure answer. The files the
    /// arguments name and zaprett creates itself (user lists, guard files) are created first: the engine refuses a missing
    /// list file, and on a fresh installation they do not exist until the first start. A failure is written to the log
    /// with the code and the output of the engine.</summary>
    public async Task<(JsonObject? Fail, JsonObject DryRun)> CheckAsync(GenerateResult g, ZaprettConfig cfg, CancellationToken ct)
    {
        c.Generator.EnsureFiles(cfg, g.Args);
        var (rc, output, missing) = await c.Generator.DryRunAsync(c.P.Processes, g.Engine, g.Args, ct).ConfigureAwait(false);
        var dr = new JsonObject { ["rc"] = rc, ["output"] = output };
        if (rc == 0)
            return (null, dr);
        c.P.Log.Warn(T.S("log.dry_run_failed", g.Engine, g.StrategyId, rc, output.Replace('\n', ' ')));
        var extra = new JsonObject { ["strategy"] = g.StrategyJson(), ["engine"] = g.Engine, ["args"] = R.Arr(g.Args), ["dry_run"] = dr.DeepClone() };
        return (missing
            ? R.Fail("engine_missing", output, extra)
            : R.Fail("dry_run_failed", T.S("svc.dry_run_failed", output), extra), dr);
    }

    /// <summary>run\status.json (generation result for "status") and run\args (for "diag").</summary>
    public void WriteStatus(GenerateResult g, JsonObject? failure = null)
    {
        var st = new JsonObject
        {
            ["generated_at"] = c.Now,
            ["ok"] = g.Ok && failure == null,
            ["error"] = failure != null ? R.Error(failure) : g.Error,
            ["message"] = failure != null ? R.Message(failure) : g.Message,
            ["engine"] = g.Engine,
            ["strategy"] = g.Ok ? g.StrategyJson() : null,
            ["warnings"] = R.Arr(g.Warnings),
            ["details"] = g.Details.DeepClone(),
            ["test_mode"] = g.TestMode,
        };
        Files.WriteJson(StatusPath, st);
        if (g.Ok)
            Files.AtomicWriteText(c.RunFile("args"), string.Join('\n', g.Args) + "\n");
    }

    public JsonObject? ReadStatus() => Files.ReadJson(StatusPath, 65536);

    /// <summary>Generates, checks and starts (or restarts) the main instance with the configured strategy.</summary>
    public async Task<JsonObject> StartMainAsync(ZaprettConfig cfg, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartUnlockedAsync(cfg, null, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<JsonObject> StartUnlockedAsync(ZaprettConfig cfg, GenerateOptions? opts, CancellationToken ct)
    {
        var g = await GenerateAsync(cfg, opts, ct).ConfigureAwait(false);
        if (!g.Ok)
        {
            WriteStatus(g);
            return g.ToFail();
        }
        var (fail, _) = await CheckAsync(g, cfg, ct).ConfigureAwait(false);
        if (fail != null)
        {
            WriteStatus(g, fail);
            return fail;
        }
        WriteStatus(g);
        var exe = Engines.Executable(c.Paths, g.Engine);
        var st = await c.P.Engine.StartAsync(Main, exe, g.Args, ct).ConfigureAwait(false);
        if (!st.Running)
        {
            lastArgs = null;
            await ApplyFirewallAsync(null, false, ct).ConfigureAwait(false);
            return R.Fail("engine_not_running", T.S("svc.engine_not_running"));
        }
        lastArgs = g.Args;
        lastEngine = g.Engine;
        await ApplyFirewallAsync(cfg, true, ct).ConfigureAwait(false);
        return R.Ok(new JsonObject { ["running"] = true, ["pid"] = st.Pid, ["warnings"] = R.Arr(g.Warnings) });
    }

    /// <summary>Runs prepared arguments (a candidate of an exclusive automatic selection) in the main instance, under the
    /// same lock as the other starts; the configured strategy is started again by <see cref="StartMainAsync"/> later.</summary>
    public async Task<EngineState> RunCandidateInMainAsync(GenerateResult g, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lastArgs = null;
            return await c.P.Engine.StartAsync(Main, Engines.Executable(c.Paths, g.Engine), g.Args, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopMainAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await c.P.Engine.StopAsync(Main, ct).ConfigureAwait(false);
            lastArgs = null;
            await ApplyFirewallAsync(null, false, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>QUIC block (UDP 443) follows main.quic_block while the engine works; it is removed when it stops.</summary>
    public async Task ApplyFirewallAsync(ZaprettConfig? cfg, bool running, CancellationToken ct)
    {
        var want = running && cfg?.QuicBlock == true;
        if (await c.P.Firewall.IsQuicBlockedAsync(ct).ConfigureAwait(false) != want)
            await c.P.Firewall.SetQuicBlockAsync(want, ct).ConfigureAwait(false);
    }

    /// <summary>Applies a saved change to the running engine: restarted only when its arguments changed. A stopped engine
    /// stays stopped (the user may have stopped it on purpose); a running selection is left alone.</summary>
    public async Task<(bool Reloaded, string? Reason)> ReloadIfNeededAsync(ZaprettConfig cfg, CancellationToken ct)
    {
        if (TestActive)
            return (false, "test_running");
        if (!Running)
            return (false, null);
        var g = await GenerateAsync(cfg, null, ct).ConfigureAwait(false);
        if (g.Ok && lastArgs != null && lastEngine == g.Engine && lastArgs.SequenceEqual(g.Args))
        {
            await ApplyFirewallAsync(cfg, true, ct).ConfigureAwait(false);
            return (false, null);
        }
        var r = await StartMainAsync(cfg, ct).ConfigureAwait(false);
        if (!R.IsOk(r))
            ReloadFailures.Value?.Add(new JsonObject { ["code"] = R.Error(r), ["message"] = R.Message(r) });
        return (R.IsOk(r), R.IsOk(r) ? null : R.Error(r));
    }

    /// <summary>Failed restarts of the current call: the saved change is kept, the engine keeps its old arguments, and the
    /// dispatcher reports it as "reload_error" instead of hiding it behind reloaded=false.</summary>
    public static readonly AsyncLocal<List<JsonObject>?> ReloadFailures = new();
}
