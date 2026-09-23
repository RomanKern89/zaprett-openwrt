using System.Text.Json.Nodes;
using Zaprett.Core.Checks;
using Zaprett.Core.Config;
using Zaprett.Core.Jobs;
using Zaprett.Core.Lists;
using Zaprett.Core.Store;
using Zaprett.Core.Strategy;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;
using Zaprett.Core.Text;

namespace Zaprett.Core;

public sealed partial class CommandDispatcher
{
    /* ---------- repository ---------- */

    JsonObject RepoList(string? type)
    {
        if (type != null && !ItemTypes.All.ContainsKey(type))
            return R.Fail("bad_type", T.S("items.bad_type", type));
        return repo.List(c.Config.Load(), type);
    }

    static readonly string[] ReloadTypes = ["nfqws", "nfqws2", "bin", "lua_lib"];

    /// <summary>After repository or subscription changes: the engine is restarted when its arguments changed, or when
    /// files it reads only at start (strategies, fakes, Lua) were replaced.</summary>
    async Task<JsonObject> AfterInstallAsync(JsonObject res, JobContext ctx)
    {
        if (!R.IsOk(res))
            return res;
        var force = R.Strings(res["types"]).Any(ReloadTypes.Contains) || (R.Str(res["type"]) is { } t && ReloadTypes.Contains(t));
        res["reloaded"] = await ReloadAfterChangeAsync(force, CancellationToken.None).ConfigureAwait(false);
        return res;
    }

    async Task<bool> ReloadAfterChangeAsync(bool force, CancellationToken ct)
    {
        if (engine.TestActive || !engine.Running)
            return false;
        var cfg = c.Config.Load();
        if (force)
            return R.IsOk(await engine.StartMainAsync(cfg, ct).ConfigureAwait(false));
        return (await engine.ReloadIfNeededAsync(cfg, ct).ConfigureAwait(false)).Reloaded;
    }

    Task<JsonObject> RepoInstall(JsonObject a)
    {
        var ids = List(a, "ids") ?? (Opt(a, "id") is { } one ? [one] : null);
        if (ids == null || ids.Count == 0)
            throw new ArgsException(T.S("arg.missing", "ids"));
        var admin = Caller.IsAdmin;
        return RunJobAsync("repo-install", a, async ctx => await AfterInstallAsync(
            await repo.InstallAsync(c.Config.Load(), ids, ctx, allowCode: admin).ConfigureAwait(false), ctx).ConfigureAwait(false));
    }

    Task<JsonObject> RepoUpgrade(JsonObject a)
    {
        var ids = Flag(a, "all") ? null : List(a, "ids");
        if (ids is { Count: 0 })
            ids = null;
        var admin = Caller.IsAdmin;
        return RunJobAsync("repo-upgrade", a, async ctx => await AfterInstallAsync(
            await repo.UpgradeAsync(c.Config.Load(), ids, ctx, allowCode: admin).ConfigureAwait(false), ctx).ConfigureAwait(false));
    }

    /// <summary>Daily run: subscriptions follow their own intervals; repository items only with repo.autoupdate.</summary>
    async Task<JsonObject> AutoupdateAsync(JobContext ctx)
    {
        var cfg = c.Config.Load();
        var s = await sources.UpdateAsync(null, ctx, dueOnly: true).ConfigureAwait(false);
        var r = cfg.Repo.Autoupdate
            ? await repo.UpgradeAsync(cfg, null, ctx).ConfigureAwait(false)
            : R.Ok(new JsonObject { ["skipped"] = true, ["message"] = T.S("repo.autoupdate_off") });
        var force = R.Strings(r["types"]).Any(ReloadTypes.Contains);
        var reloaded = await ReloadAfterChangeAsync(force, CancellationToken.None).ConfigureAwait(false);
        var body = new JsonObject { ["repo"] = r, ["sources"] = s, ["reloaded"] = reloaded };
        if (!R.IsOk(r) || !R.IsOk(s))
            return R.Fail(R.IsOk(r) ? R.Error(s)! : R.Error(r)!,
                string.Join("; ", new[] { R.IsOk(r) ? null : R.Message(r), R.IsOk(s) ? null : R.Message(s) }.Where(x => x != null)), body);
        body["message"] = T.S("repo.autoupdate_done");
        return R.Ok(body);
    }

    /* ---------- subscriptions ---------- */

    async Task<JsonObject> SourcesUpdateJobAsync(IReadOnlyList<string>? names, JobContext ctx, bool dueOnly)
    {
        var r = await sources.UpdateAsync(names, ctx, dueOnly).ConfigureAwait(false);
        r["reloaded"] = await ReloadAfterChangeAsync(false, CancellationToken.None).ConfigureAwait(false);
        return r;
    }

    Task<JsonObject> SourcesUpdate(JsonObject a)
    {
        var names = List(a, "names");
        return RunJobAsync("sources-update", a, ctx => SourcesUpdateJobAsync(names is { Count: > 0 } ? names : null, ctx, false));
    }

    /// <summary>Validated values of a subscription from sources.save: {title,type,url,interval_hours,min_entries,enabled}.</summary>
    static (JsonObject? Values, JsonObject? Fail) SourceValues(JsonObject o, SourceConfig? cur)
    {
        var type = R.Str(o["type"]) ?? cur?.Type;
        var url = R.Str(o["url"]) ?? cur?.Url;
        if (type == null || !ConfigDefaults.SourceTypes.Contains(type))
            return (null, R.Fail("bad_value", T.S("src.bad_type")));
        if (!Validate.HttpsUrlValid(url))
            return (null, R.Fail("bad_value", T.S("src.bad_url")));
        var values = new JsonObject { ["type"] = type, ["url"] = url };
        if (o["title"] != null)
        {
            var title = R.Str(o["title"]);
            if (title == null || title.Length > 128 || title.Contains('\n', StringComparison.Ordinal))
                return (null, R.Fail("bad_value", T.S("src.bad_title")));
            values["name"] = title;
        }
        foreach (var (k, min, max) in new[] { ("interval_hours", 1, 8760), ("min_entries", 0, 100000000) })
        {
            if (o[k] == null)
                continue;
            var n = R.Long(o[k]) is { } l && l >= min && l <= max ? (int?)l : Validate.ParseUint(R.Str(o[k]), min, max);
            if (n == null)
                return (null, R.Fail("bad_value", T.S("src.bad_number", k, min, max)));
            values[k] = n;
        }
        if (o["enabled"] != null)
            values["enabled"] = R.Bool(o["enabled"]) || R.Long(o["enabled"]) == 1 || R.Str(o["enabled"]) == "1";
        return (values, null);
    }

    async Task<JsonObject> SourcesSaveAsync(string name, JsonObject a, CancellationToken ct)
    {
        if (!Validate.SourceNameValid(name))
            return R.Fail("bad_id", T.S("src.bad_name"));
        if (c.Jobs.IsBusy)
            return Jobs.JobManager.BusyFail(c.Jobs.Read());
        var cfg = c.Config.Load();
        var cur = cfg.Sources.FirstOrDefault(s => s.Name == name);
        var (values, fail) = SourceValues(a, cur);
        if (fail != null)
            return fail;
        var type = R.Str(values!["type"])!;
        var iid = Sources.ItemId(name);
        var typeChanged = cur != null && cur.Type != type;
        var urlChanged = cur != null && cur.Url != R.Str(values["url"]);
        var changes = new JsonObject();
        // a changed type moves the id to the option of the new type: the subscription stays switched on
        if (typeChanged && ItemTypes.OptionOf(cur!.Type) is { } oldOpt && cfg.ListOption(oldOpt).Contains(iid))
        {
            var newOpt = ItemTypes.OptionOf(type)!;
            changes[oldOpt] = R.Arr(cfg.ListOption(oldOpt).Where(x => x != iid));
            if (!cfg.ListOption(newOpt).Contains(iid))
                changes[newOpt] = R.Arr([.. cfg.ListOption(newOpt), iid]);
        }
        // a default subscription created again is no longer "deleted by the user"
        if (cfg.DeletedSources.Contains(name))
            changes["deleted_sources"] = R.Arr(cfg.DeletedSources.Where(x => x != name));
        if (!c.Config.SetSource(name, values))
            return R.Fail("write_failed", T.S("src.save_failed"));
        if (changes.Count > 0 && !c.Config.Set("main", changes))
            return R.Fail("write_failed", T.S("src.move_failed"));
        if (typeChanged)
            sources.RemoveItem(name);
        else if (urlChanged)
            sources.ResetState(name);
        var reloaded = await ReloadAfterChangeAsync(false, ct).ConfigureAwait(false);
        return R.Ok(new JsonObject
        {
            ["name"] = name, ["item_id"] = iid, ["type"] = type, ["created"] = cur == null, ["type_changed"] = typeChanged,
            ["url_changed"] = urlChanged, ["reloaded"] = reloaded,
        });
    }

    async Task<JsonObject> SourcesDeleteAsync(string name, CancellationToken ct)
    {
        if (!Validate.SourceNameValid(name))
            return R.Fail("bad_id", T.S("src.bad_name_short"));
        if (c.Jobs.IsBusy)
            return Jobs.JobManager.BusyFail(c.Jobs.Read());
        var cfg = c.Config.Load();
        if (cfg.Sources.All(s => s.Name != name))
            return R.Fail("not_found", T.S("src.not_found", name));
        var iid = Sources.ItemId(name);
        var changes = new JsonObject();
        foreach (var t in ItemTypes.ListTypes)
        {
            var opt = ItemTypes.OptionOf(t)!;
            if (cfg.ListOption(opt).Contains(iid))
                changes[opt] = R.Arr(cfg.ListOption(opt).Where(x => x != iid));
        }
        // a deleted default subscription must not come back with an update of the program
        if (ConfigDefaults.DefaultSourceNames.Contains(name) && !cfg.DeletedSources.Contains(name))
            changes["deleted_sources"] = R.Arr([.. cfg.DeletedSources, name]);
        if ((changes.Count > 0 && !c.Config.Set("main", changes)) || !c.Config.DeleteSource(name))
            return R.Fail("write_failed", T.S("src.delete_failed"));
        var removed = sources.RemoveItem(name);
        var reloaded = await ReloadAfterChangeAsync(false, ct).ConfigureAwait(false);
        return R.Ok(new JsonObject { ["name"] = name, ["removed_item"] = removed, ["reloaded"] = reloaded });
    }

    /// <summary>"sources.defaults": default subscriptions missing from config.json are added again, except those the user
    /// deleted (main.deleted_sources) — after an update of the program.</summary>
    JsonObject SourcesDefaults()
    {
        var cfg = c.Config.Load();
        var have = cfg.Sources.Select(s => s.Name).ToHashSet();
        var added = new JsonArray();
        var skipped = new JsonArray();
        foreach (var (name, values) in ConfigDefaults.Sources())
        {
            if (have.Contains(name) || cfg.DeletedSources.Contains(name))
                continue;
            if (c.Config.SetSource(name, (JsonObject)values!))
                added.Add(name);
            else
                skipped.Add(name);
        }
        return R.Ok(new JsonObject { ["added"] = added, ["skipped"] = skipped });
    }

    /* ---------- automatic selection ---------- */

    Task<JsonObject> TestStart(JsonObject a)
    {
        var strategies = List(a, "strategies");
        var o = new TestOptions(Flag(a, "quick"), strategies is { Count: > 0 } ? strategies : null, Flag(a, "apply_if_better"), Flag(a, "exclusive"));
        return RunJobAsync("test", a, ctx => tester.RunAsync(c.Config.Load(), o, ctx));
    }

    JsonObject TestStatus(bool brief)
    {
        var j = c.Jobs.Read();
        var isTest = j != null && R.Str(j["name"]) == "test";
        var res = tester.ReadResults();
        return R.Ok(new JsonObject
        {
            ["job"] = isTest ? j : null,
            ["running"] = isTest && R.Str(j!["state"]) == "running",
            ["mode"] = res?["mode"]?.DeepClone(),
            ["mode_reason"] = res?["mode_reason"]?.DeepClone(),
            ["results"] = brief ? Tester.Brief(res) : Tester.Trim(res),
        });
    }

    async Task<JsonObject> TestStopAsync()
    {
        var j = c.Jobs.Read();
        if (j == null || R.Str(j["name"]) != "test" || R.Str(j["state"]) != "running")
        {
            if (engine.TestActive)
            {
                var rr = await tester.RestoreAsync().ConfigureAwait(false);
                return R.Ok(new JsonObject { ["state"] = "restored", ["reloaded"] = R.IsOk(rr) });
            }
            return R.Fail("no_job", T.S("test.not_running"));
        }
        return c.Jobs.Cancel();
    }

    async Task<JsonObject> TestApplyAsync(string id, CancellationToken ct)
    {
        if (!Validate.IsId(id))
            return R.Fail("bad_id", T.S("items.bad_id"));
        var cfg = c.Config.Load();
        var idx = c.Store.Scan();
        if (idx.Get(Engines.ItemType(cfg.Engine), id) == null)
            return R.Fail("strategy_not_found", T.S("strategy.not_found_engine", id, cfg.Engine));
        if (engine.TestActive)
            return R.Fail("test_running", T.S("test.running_apply"));
        var g = await engine.GenerateAsync(cfg, new GenerateOptions { Strategy = id, Index = idx }, ct).ConfigureAwait(false);
        if (!g.Ok)
            return g.ToFail();
        if (!c.Config.Set("main", new JsonObject { [Engines.StrategyOption(cfg.Engine)] = id }))
            return R.Fail("write_failed", T.S("svc.save_settings_failed"));
        var (reloaded, _) = await engine.ReloadIfNeededAsync(c.Config.Load(), ct).ConfigureAwait(false);
        return R.Ok(new JsonObject { ["strategy"] = id, ["engine"] = cfg.Engine, ["reloaded"] = reloaded, ["warnings"] = R.Arr(g.Warnings) });
    }

    /* ---------- probe and diagnosis ---------- */

    Task<JsonObject> Probe(JsonObject a)
    {
        var ids = List(a, "services");
        return RunJobAsync("probe", a, async ctx =>
        {
            var r = await health.ProbeRunAsync(c.Config.Load(), ids is { Count: > 0 } ? ids : null, ctx).ConfigureAwait(false);
            if (R.IsOk(r) && health.ProbeStatus()["probe"] is JsonObject p)
                Raise("probe", p);
            return r;
        });
    }

    Task<JsonObject> DiagnoseStart(JsonObject a)
    {
        var ids = List(a, "services");
        return RunJobAsync("diagnose", a, ctx => diagnose.RunAsync(c.Config.Load(), ids is { Count: > 0 } ? ids : null, ctx));
    }
}
