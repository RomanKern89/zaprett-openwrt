using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Engine;
using Zaprett.Core.Platform;
using Zaprett.Core.Presets;
using Zaprett.Core.Store;
using Zaprett.Core.Util;
using Zaprett.Core.Text;

namespace Zaprett.Core;

public sealed partial class CommandDispatcher
{
    public const int LogTailDefault = 200;
    public const int LogTailMax = 1000;

    public static readonly IReadOnlyDictionary<string, string[]> Pages = new Dictionary<string, string[]>
    {
        ["overview"] = ["status", "job", "presets", "monitor", "probe", "dns"],
        ["lists"] = ["status", "job", "items", "sources", "presets"],
        ["strategies"] = ["status", "job", "items", "test"],
        ["diagnostics"] = ["status", "job", "monitor", "dns", "diagnose"],
    };

    /* ---------- status ---------- */

    async Task<JsonObject> StatusAsync(CancellationToken ct)
    {
        var recovered = false;
        // an automatic selection whose job is gone (service crash) is rolled back as soon as someone looks
        if (engine.TestActive && !health.TestBusyByJob())
        {
            await tester.RestoreAsync().ConfigureAwait(false);
            recovered = true;
        }
        var cfg = c.Config.Load();
        var idx = c.Store.Scan();
        var main = c.P.Engine.GetState(EngineService.Main);
        var gen = engine.ReadStatus();
        var sid = cfg.CurrentStrategy();
        var sitem = sid.Length > 0 ? idx.Get(Engines.ItemType(cfg.Engine), sid) : null;
        var warnings = new List<string>();
        void Add(string w)
        {
            if (!warnings.Contains(w))
                warnings.Add(w);
        }
        foreach (var w in cfg.Warnings)
            Add(w);
        foreach (var w in R.Strings(gen?["warnings"]))
            if (Warnings.All.Contains(w))
                Add(w);
        if (gen != null && gen["ok"] is JsonValue okv && okv.GetValueKind() == JsonValueKind.False)
            Add("generate_failed");
        if (!File.Exists(Engines.Executable(c.Paths, cfg.Engine)))
            Add("engine_missing");
        if (sid.Length == 0)
            Add("no_strategy");
        else if (sitem == null)
            Add("strategy_missing");
        foreach (var t in ItemTypes.ListTypes)
            foreach (var id in cfg.ListOption(ItemTypes.OptionOf(t)!))
                if (!id.StartsWith("src-", StringComparison.Ordinal) && idx.Get(t, id) == null)
                    Add("list_missing");
        if (cfg.Enabled && !main.Running)
            Add("not_running");
        if (main is { Running: true, Phase: EnginePhases.WaitingNetwork })
            Add("waiting_network");
        var scanned = await Checks.Conflicts.TryBlockingAsync(c.P, ct).ConfigureAwait(false);
        var blocking = scanned ?? [];
        if (blocking.Count > 0)
            Add(Checks.Conflicts.Warning);
        if (scanned != null)
            health.NoteConflict(blocking.Count > 0, RecheckAsync, lifetime);
        if (engine.TestActive)
            Add("test_running");
        var monitor = health.MonitorBrief(cfg);
        if (R.Str(monitor?["state"]) is "degraded" or "repairing")
            Add("monitor_degraded");
        var presets = c.LoadPresets();
        if (PresetLogic.MemoryHeavy(cfg, presets, cfg.Sources, null, c.P.System.MemoryAvailableMiB))
            Add("low_memory");
        var dns = await c.P.DnsControl.GetStatusAsync(ct).ConfigureAwait(false);
        if (PresetLogic.DnsPlain(cfg, presets, R.Bool(dns["encrypted"])))
            Add("dns_plain");
        var details = new JsonObject { ["generate"] = gen, ["bad_options"] = R.Arr(cfg.BadOptions) };
        if (recovered)
            details["recovered_test"] = true;
        var job = c.Jobs.Read();
        return R.Ok(new JsonObject
        {
            ["enabled"] = cfg.Enabled,
            ["autostart"] = cfg.Autostart,
            // this core has the method "autostart" (separate from enabled); an older service does not send the field
            ["autostart_separate"] = true,
            ["running"] = main.Running,
            ["pid"] = main.Pid,
            ["engine"] = cfg.Engine,
            ["engine_version"] = await EngineVersionAsync(cfg.Engine, ct).ConfigureAwait(false),
            ["strategy"] = new JsonObject { ["id"] = sid.Length > 0 ? sid : null, ["name"] = sitem?.Name, ["source"] = sitem?.Source },
            ["list_mode"] = cfg.ListMode,
            ["lists"] = R.Arr(cfg.Lists),
            ["exclude_lists"] = R.Arr(cfg.ExcludeLists),
            ["ipsets"] = R.Arr(cfg.Ipsets),
            ["exclude_ipsets"] = R.Arr(cfg.ExcludeIpsets),
            ["quic_block"] = cfg.QuicBlock,
            ["game_filter"] = cfg.GameFilter,
            ["network_filter"] = new JsonObject
            {
                ["mode"] = cfg.NetworkFilter.Mode, ["ssids"] = R.Arr(cfg.NetworkFilter.Ssids), ["skip_corporate"] = cfg.NetworkFilter.SkipCorporate,
            },
            ["stopped"] = c.UserStopped,
            ["test_mode"] = engine.TestActive,
            ["engine_stats"] = main.Running
                ? new JsonObject
                {
                    ["pid"] = main.Pid,
                    ["uptime_s"] = main.StartedAt is { } s ? (long?)Math.Max(0, (long)(c.P.Clock.Now - s).TotalSeconds) : null,
                    ["restarts"] = main.RestartsInWindow,
                    ["phase"] = main.Phase,
                }
                : null,
            ["platform"] = await c.P.System.GetPlatformAsync(ct).ConfigureAwait(false),
            ["windivert"] = await c.P.System.GetWinDivertAsync(ct).ConfigureAwait(false),
            ["dns"] = dns,
            ["job"] = job == null ? null : new JsonObject
            {
                ["id"] = job["id"]?.DeepClone(), ["name"] = job["name"]?.DeepClone(), ["state"] = job["state"]?.DeepClone(),
                ["progress"] = job["progress"]?.DeepClone(),
            },
            ["monitor"] = monitor,
            ["conflicts_blocking"] = blocking,
            ["warnings"] = R.Arr(warnings),
            ["details"] = details,
            ["debug"] = cfg.Debug ? new JsonObject { ["until"] = cfg.DebugUntil > 0 ? cfg.DebugUntil : null } : null,
            ["version"] = c.Options.Version,
            ["install_id"] = c.InstallId,
            // of the caller of this answer (not of the service): the app greys out what this user cannot change
            ["can_modify"] = Caller.CanModify,
        });
    }

    readonly Dictionary<string, (long Size, DateTime Mtime, string? Version)> versionCache = [];

    /// <summary>"&lt;engine&gt; --version", cached by the size and time of the executable.</summary>
    async Task<string?> EngineVersionAsync(string eng, CancellationToken ct)
    {
        var exe = Engines.Executable(c.Paths, eng);
        var fi = new FileInfo(exe);
        if (!fi.Exists)
            return null;
        lock (versionCache)
            if (versionCache.TryGetValue(eng, out var cached) && cached.Size == fi.Length && cached.Mtime == fi.LastWriteTimeUtc)
                return cached.Version;
        string? v = null;
        try
        {
            var r = await c.P.Processes.RunAsync(exe, ["--version"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var text = r.StdOut + "\n" + r.StdErr;
            var m = System.Text.RegularExpressions.Regex.Match(text, "github version (v[0-9A-Za-z._-]+)");
            if (!m.Success)
                m = System.Text.RegularExpressions.Regex.Match(text, "version (v?[0-9][0-9A-Za-z._-]*)");
            v = m.Success ? m.Groups[1].Value : text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (v is { Length: > 64 })
                v = v[..64];
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            c.P.Log.Warn(T.S("svc.engine_version_failed", eng, e.Message));
        }
        lock (versionCache)
            versionCache[eng] = (fi.Length, fi.LastWriteTimeUtc, v);
        return v;
    }

    /* ---------- service control ---------- */

    async Task<JsonObject> StartAsync(CancellationToken ct)
    {
        if (engine.TestActive)
            return R.Fail("test_running", T.S("svc.test_running_wait"));
        var cfg = c.Config.Load();
        if (!cfg.Enabled)
        {
            if (!c.Config.Set("main", new JsonObject { ["enabled"] = true }))
                return R.Fail("write_failed", T.S("svc.save_enabled_failed"));
            cfg = c.Config.Load();
        }
        c.SetUserStopped(false);
        var r = await engine.StartMainAsync(cfg, ct).ConfigureAwait(false);
        RaiseStatus();
        return r;
    }

    async Task<JsonObject> StopAsync(CancellationToken ct)
    {
        if (engine.TestActive)
            return R.Fail("test_running", T.S("svc.test_running_stop"));
        c.SetUserStopped(true);
        await engine.StopMainAsync(ct).ConfigureAwait(false);
        RaiseStatus();
        if (engine.Running)
            return R.Fail("stop_failed", T.S("svc.stop_failed"));
        return R.Ok(new JsonObject { ["running"] = false });
    }

    async Task<JsonObject> RestartAsync(CancellationToken ct)
    {
        if (engine.TestActive)
            return R.Fail("test_running", T.S("svc.test_running"));
        var cfg = c.Config.Load();
        if (!cfg.Enabled)
            return R.Fail("disabled", T.S("svc.disabled"));
        c.SetUserStopped(false);
        var r = await engine.StartMainAsync(cfg, ct).ConfigureAwait(false);
        RaiseStatus();
        return r;
    }

    /// <summary>"enable" (kept for the CLI and the installer): on now (the watchdog starts the engine) and at Windows start.</summary>
    Task<JsonObject> EnableAsync(CancellationToken ct)
    {
        if (!c.Config.Set("main", new JsonObject { ["enabled"] = true, ["autostart"] = true }))
            return Task.FromResult(R.Fail("write_failed", T.S("svc.save_enabled_failed")));
        return Task.FromResult(R.Ok(new JsonObject { ["enabled"] = true, ["autostart"] = true }));
    }

    /// <summary>"disable": off now and at Windows start.</summary>
    async Task<JsonObject> DisableAsync(CancellationToken ct)
    {
        if (!c.Config.Set("main", new JsonObject { ["enabled"] = false, ["autostart"] = false }))
            return R.Fail("write_failed", T.S("svc.save_enabled_failed"));
        if (!engine.TestActive)
            await engine.StopMainAsync(ct).ConfigureAwait(false);
        RaiseStatus();
        return R.Ok(new JsonObject { ["enabled"] = false, ["autostart"] = false });
    }

    /// <summary>"autostart {enable}": only whether the bypass is switched on when Windows starts; the engine is left as it is.</summary>
    Task<JsonObject> AutostartAsync(JsonObject a)
    {
        var on = BoolArg(a, "enable") ?? throw new ArgsException(T.S("arg.missing", "enable"));
        if (!c.Config.Set("main", new JsonObject { ["autostart"] = on }))
            return Task.FromResult(R.Fail("write_failed", T.S("svc.save_settings_failed")));
        return Task.FromResult(R.Ok(new JsonObject { ["autostart"] = on, ["enabled"] = c.Config.Load().Enabled }));
    }

    /// <summary>"check": the current configuration is generated and checked by the engine without starting it.</summary>
    async Task<JsonObject> CheckAsync(CancellationToken ct)
    {
        var cfg = c.Config.Load();
        var g = await engine.GenerateAsync(cfg, null, ct).ConfigureAwait(false);
        if (!g.Ok)
            return g.ToFail();
        var (fail, dr) = await engine.CheckAsync(g, cfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        return R.Ok(new JsonObject
        {
            ["engine"] = g.Engine, ["strategy"] = g.StrategyJson(), ["args"] = R.Arr(g.Args), ["ports"] = g.PortsJson(), ["dry_run"] = dr,
            ["warnings"] = R.Arr(g.Warnings), ["details"] = g.Details.DeepClone(), ["dropped_tokens"] = R.Arr(g.Dropped),
        });
    }

    async Task<JsonObject> EnsureAsync(CancellationToken ct)
    {
        var cfg = await ExpireDebugAsync(c.Config.Load(), ct).ConfigureAwait(false);
        var r = await health.EnsureAsync(cfg, ct).ConfigureAwait(false);
        // the watchdog runs every 5 minutes also when no window asks for status: a conflict that is gone is noticed
        if (await Checks.Conflicts.TryBlockingAsync(c.P, ct).ConfigureAwait(false) is { } scanned)
            health.NoteConflict(scanned.Count > 0, RecheckAsync, lifetime);
        if (R.Str(r["action"]) == "started")
            RaiseStatus();
        return r;
    }

    /// <summary>The extra monitor check after a conflict is gone (in the background, not bound to the caller).</summary>
    Task<JsonObject> RecheckAsync(CancellationToken ct) => MonitorRunAsync(ct, true);

    async Task<JsonObject> MonitorRunAsync(CancellationToken ct, bool extra = false)
    {
        var cfg = c.Config.Load();
        var r = await health.MonitorRunAsync(cfg, () => c.Jobs.Start("test",
            ctx => tester.RunAsync(c.Config.Load(), new Checks.TestOptions(Quick: true, ApplyIfBetter: true), ctx)), ct, extra).ConfigureAwait(false);
        if (r["skipped"] == null)
            Raise("monitor", (JsonObject)health.MonitorStatus(cfg)["monitor"]!);
        return r;
    }

    /// <summary>Engine debugging is temporary (S-3): main.debug gets an end time and is switched off when it passes
    /// (checked by the watchdog every 5 minutes and at startup).</summary>
    async Task<ZaprettConfig> ExpireDebugAsync(ZaprettConfig cfg, CancellationToken ct)
    {
        if (!cfg.Debug)
            return cfg;
        if (cfg.DebugUntil == 0)
        {
            c.Config.Set("main", new JsonObject { ["debug_until"] = c.Now + c.Options.DebugMinutes * 60L });
            return c.Config.Load();
        }
        if (c.Now < cfg.DebugUntil)
            return cfg;
        c.Config.Set("main", new JsonObject { ["debug"] = false, ["debug_until"] = null });
        c.P.Log.Info(T.S("svc.debug_expired", c.Options.DebugMinutes));
        var off = c.Config.Load();
        await engine.ReloadIfNeededAsync(off, ct).ConfigureAwait(false);
        return off;
    }

    /* ---------- settings ---------- */

    JsonObject SettingsGet()
    {
        var cfg = c.Config.Load();
        return R.Ok(new JsonObject
        {
            ["settings"] = ConfigDocument.From(cfg), ["bad_options"] = R.Arr(cfg.BadOptions), ["warnings"] = R.Arr(cfg.Warnings),
        });
    }

    /// <summary>"settings.set": args is a partial config.json document (objects are merged, null removes a key). A value
    /// that is invalid is refused with bad_value and details.bad_options, the rest is not applied either.</summary>
    async Task<JsonObject> SettingsSetAsync(JsonObject patch, CancellationToken ct)
    {
        var (doc, _) = c.Config.ReadRaw();
        var oldCfg = ConfigLoader.Normalize(doc);
        var next = doc.DeepClone().AsObject();
        patch = patch.DeepClone().AsObject();
        patch.Remove("schema");
        patch.Remove("foreground");
        // the repository decides which code (Lua of winws2) the engine runs as SYSTEM: only administrators change it
        if (patch["repo"]?["url"] != null && R.Str(patch["repo"]?["url"]) != oldCfg.Repo.Url && !Caller.IsAdmin)
            return R.Fail("access_denied", T.S("call.admin_only"));
        // debugging slows the engine down many times: it always gets an end time
        if (patch["main"] is JsonObject pm && R.Bool(pm["debug"]) && !oldCfg.Debug)
            pm["debug_until"] = c.Now + c.Options.DebugMinutes * 60L;
        ConfigStore.Merge(next, patch);
        var newCfg = ConfigLoader.Normalize(next);
        var introduced = newCfg.BadOptions.Except(oldCfg.BadOptions).ToList();
        if (introduced.Count > 0)
            return R.Fail("bad_value", T.S("svc.bad_values", string.Join(", ", introduced)),
                new JsonObject { ["details"] = new JsonObject { ["bad_options"] = R.Arr(introduced) } });
        var (fail, warnings) = await ValidateChangeAsync(oldCfg, newCfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        if (!c.Config.MergePatch(patch))
            return R.Fail("write_failed", T.S("svc.save_settings_failed"));
        var saved = c.Config.Load();
        var (reloaded, _) = await engine.ReloadIfNeededAsync(saved, ct).ConfigureAwait(false);
        return R.Ok(new JsonObject
        {
            ["settings"] = ConfigDocument.From(saved), ["reloaded"] = reloaded, ["warnings"] = R.Arr(warnings),
        });
    }

    /* ---------- dns, update, log, diag, version, page ---------- */

    async Task<JsonObject> DnsSetupAsync(JsonObject a, CancellationToken ct)
    {
        var enable = a["enable"] == null || Flag(a, "enable");
        var st = await c.P.DnsControl.GetStatusAsync(ct).ConfigureAwait(false);
        if (enable && R.Bool(st["encrypted"]))
            return R.Ok(new JsonObject { ["changed"] = false, ["dns"] = st });
        var r = await c.P.DnsControl.SetupAsync(enable, ct).ConfigureAwait(false);
        if (R.IsOk(r) && !c.Config.Set("dns", new JsonObject { ["mode"] = enable ? "doh" : "system" }))
            return R.Fail("write_failed", T.S("svc.save_dns_failed"));
        return r;
    }

    async Task<JsonObject> UpdateAsync(bool install, JsonObject a, CancellationToken ct)
    {
        var u = c.Options.Updates;
        if (u == null)
            return R.Fail("not_supported", T.S("svc.update_not_supported"));
        if (install && !Caller.IsAdmin)
            return R.Fail("access_denied", T.S("call.admin_only"));
        var channel = Opt(a, "channel") ?? c.Config.Load().Update.Channel;
        if (channel is not ("stable" or "beta"))
            return R.Fail("bad_value", T.S("svc.bad_channel"));
        return install ? await u.InstallAsync(channel, ct).ConfigureAwait(false) : await u.CheckAsync(channel, ct).ConfigureAwait(false);
    }

    JsonObject Log(JsonObject a)
    {
        var n = Int(a, "tail") ?? LogTailDefault;
        if (n < 1)
            return R.Fail("bad_value", T.S("svc.bad_tail"));
        n = Math.Min(n, LogTailMax);
        var lines = c.P.Log.Tail(n);
        return R.Ok(new JsonObject { ["lines"] = R.Arr(lines.Skip(Math.Max(0, lines.Count - n))) });
    }

    JsonObject JobLog(JsonObject a)
    {
        var n = Int(a, "tail") ?? 200;
        return R.Ok(new JsonObject { ["log"] = c.Jobs.LogTail(n) });
    }

    async Task<JsonObject> VersionAsync(CancellationToken ct) => R.Ok(new JsonObject
    {
        ["version"] = c.Options.Version,
        [Engines.Winws] = await EngineVersionAsync(Engines.Winws, ct).ConfigureAwait(false),
        [Engines.Winws2] = await EngineVersionAsync(Engines.Winws2, ct).ConfigureAwait(false),
    });

    static string Section(string title, string? body) => $"===== {title} =====\n{(body ?? "").Trim()}\n\n";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Diagnostic report without secrets: subscription URLs lose their query (W-8).</summary>
    async Task<JsonObject> DiagAsync(bool full, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(Section("zaprett", $"zaprett {c.Options.Version}\nwinws: {await EngineVersionAsync(Engines.Winws, ct).ConfigureAwait(false) ?? T.S("diag.not_installed")}\n" +
            $"winws2: {await EngineVersionAsync(Engines.Winws2, ct).ConfigureAwait(false) ?? T.S("diag.not_installed")}"));
        sb.Append(Section(T.S("diag.system"), (await c.P.System.GetPlatformAsync(ct).ConfigureAwait(false)).ToJsonString(Indented)));
        sb.Append(Section("WinDivert", (await c.P.System.GetWinDivertAsync(ct).ConfigureAwait(false)).ToJsonString(Indented)));
        sb.Append(Section(T.S("diag.status"), (await StatusAsync(ct).ConfigureAwait(false)).ToJsonString(Indented)));
        var doc = ConfigDocument.From(c.Config.Load());
        foreach (var (_, s) in doc["sources"]!.AsObject())
            if (s is JsonObject so && R.Str(so["url"]) is { } u && u.Contains('?', StringComparison.Ordinal))
                so["url"] = u[..u.IndexOf('?', StringComparison.Ordinal)] + "?…";
        sb.Append(Section("config.json", doc.ToJsonString(Indented)));
        sb.Append(Section(T.S("diag.args"), Files.ReadLimited(c.RunFile("args"), 65536) ?? T.S("diag.none")));
        sb.Append(Section(T.S("diag.generate"), engine.ReadStatus()?.ToJsonString(Indented) ?? T.S("diag.none")));
        sb.Append(Section(T.S("diag.conflicts"), (await c.P.Conflicts.ScanAsync(ct).ConfigureAwait(false)).ToJsonString(Indented)));
        sb.Append(Section(T.S("diag.job"), c.Jobs.Read()?.ToJsonString(Indented) ?? "null"));
        sb.Append(Section(T.S("diag.log", full ? 100 : 30), string.Join('\n', c.P.Log.Tail(full ? 100 : 30))));
        return R.Ok(new JsonObject { ["text"] = sb.ToString(), ["full"] = full });
    }

    /// <summary>One call instead of several for a UI page: every field is the whole answer of its method.</summary>
    async Task<JsonObject> PageAsync(string name, CancellationToken ct)
    {
        if (!Pages.TryGetValue(name, out var parts))
            return R.Fail("bad_value", T.S("svc.bad_page"));
        var res = R.Ok(new JsonObject { ["page"] = name });
        foreach (var k in parts)
        {
            try
            {
                res[k] = k switch
                {
                    "status" => await StatusAsync(ct).ConfigureAwait(false),
                    "job" => R.Ok(new JsonObject { ["job"] = c.Jobs.Read() }),
                    "presets" => Presets(),
                    "monitor" => health.MonitorStatus(c.Config.Load()),
                    "probe" => health.ProbeStatus(),
                    "items" => Items(null),
                    "sources" => sources.List(),
                    "test" => TestStatus(true),
                    "dns" => R.Ok(new JsonObject { ["dns"] = await c.P.DnsControl.GetStatusAsync(ct).ConfigureAwait(false) }),
                    "diagnose" => diagnose.Status(),
                    _ => R.Fail("internal_error", T.S("svc.bad_page_part")),
                };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                res[k] = R.Fail("internal_error", T.S("call.internal_error", e.Message));
            }
        }
        return res;
    }
}
