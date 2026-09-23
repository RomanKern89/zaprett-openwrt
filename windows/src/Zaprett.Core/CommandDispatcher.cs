using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zaprett.Core.Checks;
using Zaprett.Core.Config;
using Zaprett.Core.Engine;
using Zaprett.Core.Lists;
using Zaprett.Core.Platform;
using Zaprett.Core.Repo;
using Zaprett.Core.Strategy;
using Zaprett.Core.Util;
using Zaprett.Core.Text;

namespace Zaprett.Core;

/// <summary>
/// The core entry point (ARCHITECTURE-WIN §6, §13): every IPC/CLI method with the router-compatible answer. Methods
/// that change something need <see cref="CallerInfo.CanModify"/> ({ok:false, error:"access_denied"} otherwise).
/// Background work (repository, subscriptions, automatic selection, probe, diagnosis) runs as jobs of the core's own
/// <see cref="Jobs.JobManager"/>; the service calls <see cref="StartupAsync"/> once, <c>ensure</c> every 5 minutes (when
/// main.watchdog is on), <c>monitor.run</c> every monitor.interval minutes and <c>autoupdate</c> daily at
/// repo.autoupdate_hour — these three are internal methods (CanModify required) and not part of the UI protocol.
/// </summary>
public sealed partial class CommandDispatcher : ICommandDispatcher
{
    static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal)
    {
        "status", "items", "strategy.show", "user.get", "check", "repo.list", "presets", "sources.list", "test.status", "job.status",
        "job.log", "probe.status", "monitor.status", "diagnose.status", "dns.status", "log", "diag", "version", "page", "conflicts",
        "settings.get", "update.check",
    };

    readonly CoreContext c;
    readonly EngineService engine;
    readonly Health health;
    readonly Tester tester;
    readonly Diagnose diagnose;
    readonly Sources sources;
    readonly RepoClient repo;
    readonly Dictionary<string, Func<JsonObject, CancellationToken, Task<JsonObject>>> methods;

    public CommandDispatcher(PlatformServices platform, CoreOptions? options = null)
    {
        c = new CoreContext(platform, options);
        engine = new EngineService(c);
        health = new Health(c, engine);
        tester = new Tester(c, engine);
        diagnose = new Diagnose(c, engine);
        sources = new Sources(c);
        repo = new RepoClient(c);
        c.Jobs.Changed += j => Raise("job", j);
        methods = BuildMethods();
    }

    public IReadOnlySet<string> ReadOnlyMethods => ReadOnly;

    /// <summary>All methods the dispatcher knows (UI methods and the internal ones).</summary>
    public IReadOnlyCollection<string> Methods => methods.Keys;

    public CoreContext Context => c;

    public event Action<string, JsonObject>? Event;

    void Raise(string type, JsonObject data)
    {
        try
        {
            Event?.Invoke(type, data);
        }
        catch (Exception e)
        {
            c.P.Log.Warn(T.S("log.event_handler", type, e.Message));
        }
    }

    static readonly AsyncLocal<CallerInfo?> CurrentCaller = new();

    /// <summary>Who called the current method (the service itself for internal calls).</summary>
    static CallerInfo Caller => CurrentCaller.Value ?? CallerInfo.System;

    public async Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct)
    {
        // language of every text of this call and of the jobs it starts: argument "lang", otherwise ui.language
        args = args?.DeepClone().AsObject() ?? [];
        var lang = args["lang"];
        args.Remove("lang");
        if (lang != null && T.Normalize(R.Str(lang)) == null)
        {
            T.Use(c.Config.Load().Language);
            return R.Fail("bad_value", T.S("call.bad_lang"));
        }
        T.Use(lang != null ? R.Str(lang) : c.Config.Load().Language);
        CurrentCaller.Value = caller;
        if (!methods.TryGetValue(method ?? "", out var fn))
            return R.Fail("unknown_method", T.S("call.unknown_method", method));
        if (!ReadOnly.Contains(method!) && !caller.CanModify)
            return R.Fail("access_denied", T.S("call.access_denied"));
        try
        {
            var failures = new List<JsonObject>();
            EngineService.ReloadFailures.Value = failures;
            var res = await fn(args, ct).ConfigureAwait(false);
            if (failures.Count > 0 && R.IsOk(res) && res["reload_error"] == null)
                res["reload_error"] = failures[^1].DeepClone();
            return res;
        }
        catch (ArgsException e)
        {
            return R.Fail("bad_args", e.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return R.Fail("cancelled", T.S("call.cancelled"));
        }
        catch (Exception e)
        {
            c.P.Log.Error($"{method}: {e}");
            return R.Fail("internal_error", T.S("call.internal_error", e.Message));
        }
    }

    /// <summary>Called once when the service starts: an automatic selection the service lost is rolled back, then the
    /// engine is started when the configuration says so and the user did not stop it.</summary>
    public async Task<JsonObject> StartupAsync(CancellationToken ct)
    {
        var recovered = false;
        if (engine.TestActive)
        {
            await tester.RestoreAsync().ConfigureAwait(false);
            recovered = true;
            c.P.Log.Warn(T.S("log.test_recovered"));
        }
        var cfg = c.Config.Load();
        T.Use(cfg.Language);
        cfg = await ExpireDebugAsync(cfg, ct).ConfigureAwait(false);
        if (!cfg.Enabled || c.UserStopped || engine.Running)
            return R.Ok(new JsonObject { ["started"] = false, ["recovered_test"] = recovered });
        var r = await engine.StartMainAsync(cfg, ct).ConfigureAwait(false);
        r["recovered_test"] = recovered;
        RaiseStatus();
        return r;
    }

    Dictionary<string, Func<JsonObject, CancellationToken, Task<JsonObject>>> BuildMethods() => new(StringComparer.Ordinal)
    {
        ["status"] = (a, ct) => StatusAsync(ct),
        ["start"] = (a, ct) => StartAsync(ct),
        ["stop"] = (a, ct) => StopAsync(ct),
        ["restart"] = (a, ct) => RestartAsync(ct),
        ["enable"] = (a, ct) => EnableAsync(ct),
        ["disable"] = (a, ct) => DisableAsync(ct),
        ["check"] = (a, ct) => CheckAsync(ct),
        ["items"] = (a, ct) => Task.FromResult(Items(Opt(a, "type"))),
        ["list.enable"] = (a, ct) => ListToggleAsync(Req(a, "id"), true, ct),
        ["list.disable"] = (a, ct) => ListToggleAsync(Req(a, "id"), false, ct),
        ["strategy.set"] = (a, ct) => StrategySetAsync(Req(a, "id"), ct),
        ["strategy.show"] = (a, ct) => StrategyShowAsync(Req(a, "id"), ct),
        ["strategy.save"] = (a, ct) => StrategySaveAsync(Req(a, "id"), Opt(a, "text"), ct),
        ["strategy.delete"] = (a, ct) => Task.FromResult(StrategyDelete(Req(a, "id"))),
        ["user.get"] = (a, ct) => Task.FromResult(UserGet(Req(a, "id"))),
        ["user.set"] = (a, ct) => UserSetAsync(Req(a, "id"), Opt(a, "text"), ct),
        ["mode"] = (a, ct) => SetModeAsync(Req(a, "mode"), ct),
        ["engine"] = (a, ct) => SetEngineAsync(Req(a, "engine"), ct),
        ["repo.fetch"] = (a, ct) => RunJobAsync("repo-fetch", a, ctx => repo.FetchAsync(c.Config.Load(), ctx)),
        ["repo.list"] = (a, ct) => Task.FromResult(RepoList(Opt(a, "type"))),
        ["repo.install"] = (a, ct) => RepoInstall(a),
        ["repo.remove"] = (a, ct) => RunJobAsync("repo-remove", a, ctx => AfterInstallAsync(repo.Remove(c.Config.Load(), Req(a, "id")), ctx)),
        ["repo.upgrade"] = (a, ct) => RepoUpgrade(a),
        ["sources.list"] = (a, ct) => Task.FromResult(sources.List()),
        ["sources.update"] = (a, ct) => SourcesUpdate(a),
        ["sources.save"] = (a, ct) => SourcesSaveAsync(Req(a, "name"), a, ct),
        ["sources.delete"] = (a, ct) => SourcesDeleteAsync(Req(a, "name"), ct),
        ["sources.defaults"] = (a, ct) => Task.FromResult(SourcesDefaults()),
        ["presets"] = (a, ct) => Task.FromResult(Presets()),
        ["wizard.apply"] = (a, ct) => WizardApplyAsync(List(a, "services") ?? [], ct),
        ["test.start"] = (a, ct) => TestStart(a),
        ["test.status"] = (a, ct) => Task.FromResult(TestStatus(Flag(a, "brief"))),
        ["test.stop"] = (a, ct) => TestStopAsync(),
        ["test.apply"] = (a, ct) => TestApplyAsync(Req(a, "id"), ct),
        ["job.status"] = (a, ct) => Task.FromResult(R.Ok(new JsonObject { ["job"] = c.Jobs.Read() })),
        ["job.log"] = (a, ct) => Task.FromResult(JobLog(a)),
        ["job.cancel"] = (a, ct) => Task.FromResult(c.Jobs.Cancel()),
        ["probe"] = (a, ct) => Probe(a),
        ["probe.status"] = (a, ct) => Task.FromResult(health.ProbeStatus()),
        ["monitor.status"] = (a, ct) => Task.FromResult(health.MonitorStatus(c.Config.Load())),
        ["diagnose"] = (a, ct) => DiagnoseStart(a),
        ["diagnose.status"] = (a, ct) => Task.FromResult(diagnose.Status()),
        ["dns.status"] = async (a, ct) => R.Ok(new JsonObject { ["dns"] = await c.P.DnsControl.GetStatusAsync(ct).ConfigureAwait(false) }),
        ["dns.setup"] = (a, ct) => DnsSetupAsync(a, ct),
        ["log"] = (a, ct) => Task.FromResult(Log(a)),
        ["diag"] = (a, ct) => DiagAsync(Flag(a, "full"), ct),
        ["version"] = (a, ct) => VersionAsync(ct),
        ["page"] = (a, ct) => PageAsync(Req(a, "name"), ct),
        ["conflicts"] = async (a, ct) => R.Ok(new JsonObject { ["items"] = await c.P.Conflicts.ScanAsync(ct).ConfigureAwait(false) }),
        ["settings.get"] = (a, ct) => Task.FromResult(SettingsGet()),
        ["settings.set"] = (a, ct) => SettingsSetAsync(a, ct),
        ["update.check"] = (a, ct) => UpdateAsync(false, a, ct),
        ["update.install"] = (a, ct) => UpdateAsync(true, a, ct),
        // internal: the service's timers
        ["ensure"] = (a, ct) => EnsureAsync(ct),
        ["monitor.run"] = (a, ct) => MonitorRunAsync(ct),
        ["autoupdate"] = (a, ct) => RunJobAsync("autoupdate", a, AutoupdateAsync),
    };

    /* ---------- arguments ---------- */

    sealed class ArgsException(string message) : Exception(message);

    static string? Opt(JsonObject a, string key)
    {
        var n = a[key];
        if (n == null)
            return null;
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return v.GetValue<string>();
        throw new ArgsException(T.S("arg.must_be_string", key));
    }

    static string Req(JsonObject a, string key) => Opt(a, key) ?? throw new ArgsException(T.S("arg.missing", key));

    static bool Flag(JsonObject a, string key) => a[key] is JsonValue v && (v.GetValueKind() == JsonValueKind.True ||
        (v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is "1" or "true"));

    /// <summary>A list argument: JSON array of strings or a comma-separated string; null when absent.</summary>
    static List<string>? List(JsonObject a, string key)
    {
        var n = a[key];
        if (n == null)
            return null;
        if (n is JsonArray arr)
            return arr.Select(x => R.Str(x) ?? throw new ArgsException(T.S("arg.string_array", key))).ToList();
        if (R.Str(n) is { } s)
            return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        throw new ArgsException(T.S("arg.string_array", key));
    }

    static int? Int(JsonObject a, string key)
    {
        var n = a[key];
        if (n == null)
            return null;
        if (R.Long(n) is { } l && l is >= int.MinValue and <= int.MaxValue)
            return (int)l;
        if (R.Str(n) is { } s && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
            return i;
        throw new ArgsException(T.S("arg.must_be_number", key));
    }

    /* ---------- shared helpers ---------- */

    /// <summary>Starts a job in the background, or runs it in place with {"foreground": true}.</summary>
    async Task<JsonObject> RunJobAsync(string name, JsonObject a, Func<Jobs.JobContext, Task<JsonObject>> handler)
    {
        if (Flag(a, "foreground"))
            return await c.Jobs.RunForegroundAsync(name, handler).ConfigureAwait(false);
        return c.Jobs.Start(name, handler);
    }

    /// <summary>Refuses a configuration change that would make a working configuration fail (router validate_change);
    /// both failing → allowed with warning config_was_invalid. Returns the generation result or the failure.</summary>
    async Task<(JsonObject? Fail, List<string> Warnings)> ValidateChangeAsync(ZaprettConfig oldCfg, ZaprettConfig newCfg, CancellationToken ct)
    {
        var g = await engine.GenerateAsync(newCfg, null, ct).ConfigureAwait(false);
        JsonObject? fail = g.Ok ? null : g.ToFail();
        if (g.Ok)
        {
            var (f, _) = await engine.CheckAsync(g, newCfg, ct).ConfigureAwait(false);
            fail = f;
        }
        if (fail == null)
            return (null, g.Warnings);
        var before = await engine.GenerateAsync(oldCfg, null, ct).ConfigureAwait(false);
        var beforeOk = before.Ok && (await engine.CheckAsync(before, oldCfg, ct).ConfigureAwait(false)).Fail == null;
        return beforeOk ? (fail, []) : (null, ["config_was_invalid"]);
    }

    void RaiseStatus()
    {
        var st = engine.Running;
        Raise("status", new JsonObject { ["running"] = st, ["pid"] = c.P.Engine.GetState(EngineService.Main).Pid });
    }
}
