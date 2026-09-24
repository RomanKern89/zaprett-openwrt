using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Lists;
using Zaprett.Core.Presets;
using Zaprett.Core.Store;
using Zaprett.Core.Strategy;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;
using Zaprett.Core.Text;

namespace Zaprett.Core;

public sealed partial class CommandDispatcher
{
    JsonObject Items(string? type)
    {
        if (type != null && !ItemTypes.All.ContainsKey(type))
            return R.Fail("bad_type", T.S("items.bad_type", type));
        var idx = c.Store.Scan();
        return R.Ok(new JsonObject
        {
            ["items"] = c.Store.Describe(idx, c.Config.Load(), type),
            ["errors"] = R.Arr(idx.Errors.Select(e => (JsonNode?)new JsonObject { ["path"] = e.Path, ["error"] = e.Error })),
        });
    }

    async Task<JsonObject> ListToggleAsync(string id, bool enable, CancellationToken ct)
    {
        if (!Validate.IsId(id))
            return R.Fail("bad_id", T.S("items.bad_id"));
        var idx = c.Store.Scan();
        var found = idx.FindAll(id, ItemTypes.ListTypes).Select(i => i.Type).ToList();
        // a subscription can be switched on before its first download: the type comes from its section
        if (found.Count == 0 && id.StartsWith("src-", StringComparison.Ordinal))
        {
            var src = c.Config.Load().Sources.FirstOrDefault(s => s.Name == id[4..] && s.Valid);
            if (src != null)
                found.Add(src.Type);
        }
        if (found.Count == 0)
            return R.Fail("not_found", T.S("list.not_found", id));
        if (found.Count > 1)
            return R.Fail("ambiguous_id", T.S("list.ambiguous", id));
        var type = found[0];
        var opt = ItemTypes.OptionOf(type)!;
        var cfg = c.Config.Load();
        var cur = cfg.ListOption(opt);
        var arr = cur.Where(x => x != id).ToList();
        if (enable)
            arr.Add(id);
        if (arr.Count == cur.Count && cur.Contains(id) == enable)
            return R.Ok(new JsonObject { ["id"] = id, ["type"] = type, ["enabled"] = enable, ["changed"] = false });
        var change = new JsonObject { [opt] = R.Arr(arr) };
        var ncfg = c.Config.Preview("main", change);
        var (fail, warnings) = await ValidateChangeAsync(cfg, ncfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        if (!c.Config.Set("main", change))
            return R.Fail("write_failed", T.S("svc.save_settings_failed"));
        var (reloaded, _) = await engine.ReloadIfNeededAsync(ncfg, ct).ConfigureAwait(false);
        return R.Ok(new JsonObject
        {
            ["id"] = id, ["type"] = type, ["enabled"] = enable, ["changed"] = true, ["reloaded"] = reloaded, ["warnings"] = R.Arr(warnings),
        });
    }

    async Task<JsonObject> StrategySetAsync(string id, CancellationToken ct)
    {
        if (!Validate.IsId(id))
            return R.Fail("bad_id", T.S("items.bad_id"));
        var cfg = c.Config.Load();
        var idx = c.Store.Scan();
        if (idx.Get(Engines.ItemType(cfg.Engine), id) == null)
            return R.Fail("strategy_not_found", T.S("strategy.not_found_engine", id, cfg.Engine));
        var g = await engine.GenerateAsync(cfg, new GenerateOptions { Strategy = id, Index = idx }, ct).ConfigureAwait(false);
        if (!g.Ok)
            return g.ToFail();
        var (fail, _) = await engine.CheckAsync(g, cfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        var change = new JsonObject { [Engines.StrategyOption(cfg.Engine)] = id };
        if (!c.Config.Set("main", change))
            return R.Fail("write_failed", T.S("svc.save_settings_failed"));
        var (reloaded, _) = await engine.ReloadIfNeededAsync(c.Config.Load(), ct).ConfigureAwait(false);
        return R.Ok(new JsonObject { ["id"] = id, ["engine"] = cfg.Engine, ["reloaded"] = reloaded, ["warnings"] = R.Arr(g.Warnings) });
    }

    async Task<JsonObject> StrategyShowAsync(string id, CancellationToken ct)
    {
        if (!Validate.IsId(id))
            return R.Fail("bad_id", T.S("items.bad_id"));
        var cfg = c.Config.Load();
        var idx = c.Store.Scan();
        var item = idx.Get(Engines.ItemType(cfg.Engine), id) ?? idx.Find(id, ["nfqws", "nfqws2", "byedpi"]);
        if (item == null)
            return R.Fail("strategy_not_found", T.S("strategy.not_found", id));
        var text = ItemStore.ReadStrategyText(item);
        if (text == null)
            return R.Fail("strategy_unreadable", T.S("strategy.unreadable"));
        var res = R.Ok(new JsonObject
        {
            ["id"] = id, ["type"] = item.Type, ["name"] = item.Name, ["source"] = item.Source, ["description"] = item.Description,
            ["dependencies"] = R.Arr(item.Dependencies), ["text"] = text, ["args"] = null, ["ports"] = null,
        });
        if (item.Type is "nfqws" or "nfqws2")
        {
            var eng = item.Type == "nfqws2" ? Engines.Winws2 : Engines.Winws;
            var b = await engine.GenerateAsync(cfg, new GenerateOptions { Engine = eng, Strategy = id, Index = idx }, ct).ConfigureAwait(false);
            if (b.Ok)
            {
                res["args"] = R.Arr(b.Args);
                res["ports"] = b.PortsJson();
                res["warnings"] = R.Arr(b.Warnings);
            }
            else
            {
                res["build_error"] = b.Error;
                res["build_message"] = b.Message;
            }
        }
        return res;
    }

    async Task<JsonObject> StrategySaveAsync(string id, string? text, CancellationToken ct)
    {
        if (!Validate.IsId(id) || !id.StartsWith("user-", StringComparison.Ordinal) || id.Length < 6)
            return R.Fail("bad_id", T.S("strategy.bad_user_id"));
        if (text == null || Files.Utf8NoBom.GetByteCount(text) > ItemStore.MaxStrategyBytes)
            return R.Fail("too_large", T.S("strategy.too_large"));
        var cfg = c.Config.Load();
        var eng = cfg.Engine;
        var item = new StoreItem(id, Engines.ItemType(eng), id, "", "", "", null, null, [], "", "user", null, null, null, null, null);
        var g = await engine.GenerateAsync(cfg, new GenerateOptions { Engine = eng, Text = text, Item = item }, ct).ConfigureAwait(false);
        if (!g.Ok)
            return g.ToFail();
        var (fail, _) = await engine.CheckAsync(g, cfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        if (!Files.AtomicWriteText(c.Store.UserStrategyPath(eng, id), text))
            return R.Fail("write_failed", T.S("strategy.save_failed"));
        var reloaded = cfg.CurrentStrategy(eng) == id && (await engine.ReloadIfNeededAsync(cfg, ct).ConfigureAwait(false)).Reloaded;
        return R.Ok(new JsonObject
        {
            ["id"] = id, ["engine"] = eng, ["args"] = R.Arr(g.Args), ["ports"] = g.PortsJson(), ["warnings"] = R.Arr(g.Warnings),
            ["reloaded"] = reloaded,
        });
    }

    JsonObject StrategyDelete(string id)
    {
        if (!Validate.IsId(id) || !id.StartsWith("user-", StringComparison.Ordinal))
            return R.Fail("bad_id", T.S("strategy.delete_only_user"));
        var cfg = c.Config.Load();
        var deleted = new List<string>();
        foreach (var eng in new[] { Engines.Winws, Engines.Winws2 })
        {
            var p = c.Store.UserStrategyPath(eng, id);
            if (!File.Exists(p))
                continue;
            if (cfg.CurrentStrategy(eng) == id)
                return R.Fail("item_active", T.S("strategy.active", id));
            Files.TryDelete(p);
            deleted.Add(eng);
        }
        if (deleted.Count == 0)
            return R.Fail("not_found", T.S("strategy.user_not_found", id));
        return R.Ok(new JsonObject { ["id"] = id, ["engines"] = R.Arr(deleted) });
    }

    static string UserListIds => T.S("user.only_ids");

    JsonObject UserGet(string id)
    {
        if (!ItemTypes.UserLists.TryGetValue(id, out var u))
            return R.Fail("bad_id", UserListIds);
        var text = Files.ReadLimited(c.Store.UserListPath(id), Validate.MaxUserListBytes + 1) ?? "";
        return R.Ok(new JsonObject
        {
            ["id"] = id, ["type"] = u.Type, ["text"] = text, ["entries"] = Validate.CountEntries(text),
            ["size"] = Files.Utf8NoBom.GetByteCount(text),
        });
    }

    async Task<JsonObject> UserSetAsync(string id, string? text, CancellationToken ct)
    {
        if (!ItemTypes.UserLists.TryGetValue(id, out var u))
            return R.Fail("bad_id", UserListIds);
        if (text == null)
            return R.Fail("too_large", T.S("user.too_large"));
        var v = Validate.ValidateListText(ItemTypes.All[u.Type].Kind, text);
        if (!v.Ok)
            return R.Fail(v.Errors.Any(e => e.Reason == "too_large") ? "too_large" : "invalid_entries",
                T.S("user.invalid_entries", v.ErrorCount),
                new JsonObject
                {
                    ["errors"] = R.Arr(v.Errors.Select(e => (JsonNode?)new JsonObject { ["line"] = e.Line, ["value"] = e.Value, ["reason"] = e.Reason })),
                });
        var path = c.Store.UserListPath(id);
        var before = Validate.CountEntries(Files.ReadLimited(path, Validate.MaxUserListBytes + 1) ?? "");
        if (!Files.AtomicWriteText(path, v.Text))
            return R.Fail("write_failed", T.S("user.save_failed"));
        var cfg = c.Config.Load();
        var reloaded = false;
        // the engine re-reads list files by itself; regeneration matters only when a list turns empty/non-empty
        if (cfg.ListOption(ItemTypes.OptionOf(u.Type)!).Contains(id) && (before == 0) != (v.Entries == 0))
            reloaded = (await engine.ReloadIfNeededAsync(cfg, ct).ConfigureAwait(false)).Reloaded;
        return R.Ok(new JsonObject { ["id"] = id, ["entries"] = v.Entries, ["size"] = Files.Utf8NoBom.GetByteCount(v.Text), ["reloaded"] = reloaded });
    }

    async Task<JsonObject> SetModeAsync(string mode, CancellationToken ct)
    {
        if (mode is not ("whitelist" or "blacklist"))
            return R.Fail("bad_value", T.S("mode.bad"));
        var cfg = c.Config.Load();
        var change = new JsonObject { ["list_mode"] = mode };
        var ncfg = c.Config.Preview("main", change);
        var (fail, warnings) = await ValidateChangeAsync(cfg, ncfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        if (!c.Config.Set("main", change))
            return R.Fail("write_failed", T.S("svc.save_settings_failed"));
        var (reloaded, _) = await engine.ReloadIfNeededAsync(ncfg, ct).ConfigureAwait(false);
        return R.Ok(new JsonObject { ["list_mode"] = mode, ["reloaded"] = reloaded, ["warnings"] = R.Arr(warnings) });
    }

    async Task<JsonObject> SetEngineAsync(string eng, CancellationToken ct)
    {
        if (!Engines.IsValid(eng))
            return R.Fail("bad_value", T.S("engine.bad"));
        if (!File.Exists(Engines.Executable(c.Paths, eng)))
            return R.Fail("engine_missing", T.S("svc.engine_missing_component", eng));
        var cfg = c.Config.Load();
        var change = new JsonObject { ["engine"] = eng };
        // no strategy chosen for this engine yet: the default of the presets, when it is installed
        var sopt = Engines.StrategyOption(eng);
        var d = PresetLogic.DefaultStrategy(c.LoadPresets(), eng);
        if (cfg.CurrentStrategy(eng).Length == 0 && d != null && c.Store.Scan().Get(Engines.ItemType(eng), d) != null)
            change[sopt] = d;
        var ncfg = c.Config.Preview("main", change);
        var (fail, warnings) = await ValidateChangeAsync(cfg, ncfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        if (!c.Config.Set("main", change))
            return R.Fail("write_failed", T.S("svc.save_settings_failed"));
        var (reloaded, _) = await engine.ReloadIfNeededAsync(ncfg, ct).ConfigureAwait(false);
        var s = ncfg.CurrentStrategy(eng);
        return R.Ok(new JsonObject { ["engine"] = eng, ["strategy"] = s.Length > 0 ? s : null, ["reloaded"] = reloaded, ["warnings"] = R.Arr(warnings) });
    }

    /* ---------- presets and the wizard ---------- */

    JsonObject Presets()
    {
        var p = c.LoadPresets();
        if (p == null)
            return R.Fail("presets_missing", T.S("presets.missing_path", c.Paths.PresetsFile));
        var cfg = c.Config.Load();
        var idx = c.Store.Scan();
        var srcs = cfg.Sources.ToDictionary(s => s.Name);
        int Available(ServiceSet set) =>
            set.Lists.Count(x => idx.Get("list", x) != null) + set.Ipsets.Count(x => idx.Get("ipset", x) != null) +
            set.Sources.Count(n => srcs.TryGetValue(n, out var s) && s.Valid);
        var services = new JsonArray();
        foreach (var s in PresetLogic.Services(p))
        {
            if (!Validate.IsId(R.Str(s["id"])))
                continue;
            var sets = PresetLogic.Sets(s);
            var core = sets[0];
            var active = PresetLogic.SetActive(core, cfg, srcs);
            var works = R.Str(s["works"]);
            var variants = new JsonArray();
            foreach (var v in sets.Skip(1))
                variants.Add(new JsonObject
                {
                    ["id"] = v.Id, ["name"] = v.Name?.DeepClone(), ["name_en"] = v.NameEn?.DeepClone(), ["description"] = v.Description?.DeepClone(),
                    ["description_en"] = v.DescriptionEn?.DeepClone(), ["lists"] = R.Arr(v.Lists), ["ipsets"] = R.Arr(v.Ipsets),
                    ["sources"] = R.Arr(v.Sources), ["tier"] = v.Tier,
                    ["enabled"] = v.Size > 0 && PresetLogic.SetActive(v, cfg, srcs) == v.Size,
                    ["available"] = v.Size > 0 && Available(v) == v.Size && works != "no",
                });
            services.Add(new JsonObject
            {
                ["id"] = s["id"]?.DeepClone(), ["name"] = s["name"]?.DeepClone(), ["description"] = s["description"]?.DeepClone(),
                ["note"] = s["note"]?.DeepClone(), ["tier"] = s["tier"]?.DeepClone(), ["works"] = s["works"]?.DeepClone(),
                ["name_en"] = s["name_en"]?.DeepClone(), ["description_en"] = s["description_en"]?.DeepClone(), ["note_en"] = s["note_en"]?.DeepClone(),
                ["needs_dns"] = s["needs_dns"]?.DeepClone(),
                ["lists"] = R.Arr(core.Lists), ["ipsets"] = R.Arr(core.Ipsets), ["sources"] = R.Arr(core.Sources),
                ["test_targets"] = s["test_targets"]?.DeepClone() ?? new JsonArray(),
                ["enabled"] = core.Size > 0 && active == core.Size,
                ["partially_enabled"] = active > 0 && active < core.Size,
                ["available"] = core.Size > 0 && Available(core) == core.Size && works != "no",
                ["variants"] = variants,
                ["enabled_variant"] = PresetLogic.EnabledVariant(s, cfg, srcs),
            });
        }
        return R.Ok(new JsonObject
        {
            ["schema"] = p["schema"]?.DeepClone(), ["services"] = services, ["defaults"] = p["defaults"]?.DeepClone() ?? new JsonObject(),
            ["always"] = p["always"]?.DeepClone() ?? new JsonObject(), ["tiers"] = p["tiers"]?.DeepClone() ?? new JsonObject(),
            ["ram_total_mib"] = null, ["recommended_tier"] = "full",
        });
    }

    /// <summary>refs: "&lt;service&gt;" or "&lt;service&gt;:&lt;variant&gt;" (router contract v1.7 §16.4). The chosen set of every
    /// selected service is switched on; every other set of every preset service is switched off, except items a chosen
    /// set uses too; user-* lists are never touched.</summary>
    async Task<JsonObject> WizardApplyAsync(List<string> refs, bool? autostart, CancellationToken ct)
    {
        var p = c.LoadPresets();
        if (p == null)
            return R.Fail("presets_missing", T.S("presets.missing"));
        if (refs.Count == 0)
            return R.Fail("bad_args", T.S("wizard.no_services"));
        var byId = PresetLogic.Services(p).Where(s => Validate.IsId(R.Str(s["id"]))).GroupBy(s => R.Str(s["id"])!)
            .ToDictionary(g => g.Key, g => g.First());
        var selected = new List<string>();
        var chosen = new Dictionary<string, ServiceSet>();
        var skipped = new JsonArray();
        foreach (var rf in refs)
        {
            if (PresetLogic.ParseServiceRef(rf) is not { } r)
                return R.Fail("bad_args", T.S("wizard.bad_ref", rf));
            if (!byId.TryGetValue(r.Id, out var s))
                return R.Fail("unknown_service", T.S("wizard.unknown_service", r.Id));
            var set = PresetLogic.Sets(s).FirstOrDefault(x => x.Id == r.Variant);
            if (set == null)
                return R.Fail("unknown_variant", T.S("wizard.unknown_variant", r.Id, r.Variant));
            if (chosen.TryGetValue(r.Id, out var prev) && prev.Id != set.Id)
                return R.Fail("bad_args", T.S("wizard.variant_conflict", r.Id));
            if (R.Str(s["works"]) == "no")
            {
                skipped.Add(new JsonObject { ["id"] = r.Id, ["reason"] = "works_no" });
                continue;
            }
            if (set.Size == 0)
                return R.Fail("preset_unavailable", T.S("wizard.nothing_in_set", rf));
            if (chosen.TryAdd(r.Id, set))
                selected.Add(r.Id);
        }
        if (selected.Count == 0)
            return R.Fail("preset_unavailable", T.S("wizard.all_skipped"),
                new JsonObject { ["skipped"] = skipped });

        var cfg = c.Config.Load();
        var idx = c.Store.Scan();
        var srcs = cfg.Sources.ToDictionary(s => s.Name);
        var opts = new Dictionary<string, List<string>>
        {
            ["lists"] = [.. cfg.Lists], ["ipsets"] = [.. cfg.Ipsets], ["exclude_lists"] = [.. cfg.ExcludeLists], ["exclude_ipsets"] = [.. cfg.ExcludeIpsets],
        };
        void Add(string opt, string x)
        {
            if (!opts[opt].Contains(x))
                opts[opt].Add(x);
        }
        void Drop(string opt, string x)
        {
            if (!x.StartsWith("user-", StringComparison.Ordinal))
                opts[opt].Remove(x);
        }
        var used = selected.SelectMany(id => chosen[id].Sources).ToHashSet();
        var disable = new List<string>();
        foreach (var (id, s) in byId)
            foreach (var set in PresetLogic.Sets(s))
            {
                if (chosen.TryGetValue(id, out var ch) && ch.Id == set.Id)
                    continue;
                foreach (var l in set.Lists)
                    Drop("lists", l);
                foreach (var l in set.Ipsets)
                    Drop("ipsets", l);
                foreach (var n in set.Sources)
                {
                    if (!srcs.TryGetValue(n, out var src))
                        continue;
                    Drop(ItemTypes.OptionOf(src.Type) ?? "lists", Sources.ItemId(n));
                    if (!used.Contains(n) && src.Enabled && !disable.Contains(n))
                        disable.Add(n);
                }
            }
        var missing = new List<string>();
        var enableSources = new List<string>();
        foreach (var id in selected)
        {
            var set = chosen[id];
            foreach (var l in set.Lists)
            {
                if (idx.Get("list", l) == null)
                    missing.Add(l);
                else
                    Add("lists", l);
            }
            foreach (var l in set.Ipsets)
            {
                if (idx.Get("ipset", l) == null)
                    missing.Add(l);
                else
                    Add("ipsets", l);
            }
            foreach (var n in set.Sources)
            {
                if (!srcs.TryGetValue(n, out var src) || !src.Valid)
                {
                    missing.Add("source:" + n);
                    continue;
                }
                Add(ItemTypes.OptionOf(src.Type)!, Sources.ItemId(n));
                if (!enableSources.Contains(n))
                    enableSources.Add(n);
            }
        }
        // "always" exclusions of the presets are added and never removed here
        foreach (var l in R.Strings(p["always"]?["exclude_lists"]))
        {
            if (idx.Get("list_exclude", l) == null)
                missing.Add(l);
            else
                Add("exclude_lists", l);
        }
        foreach (var l in R.Strings(p["always"]?["exclude_ipsets"]))
        {
            if (idx.Get("ipset_exclude", l) == null)
                missing.Add(l);
            else
                Add("exclude_ipsets", l);
        }
        if (missing.Count > 0)
            return R.Fail("preset_item_missing", T.S("wizard.items_missing", string.Join(", ", missing)),
                new JsonObject { ["missing"] = R.Arr(missing) });

        var change = new JsonObject { ["list_mode"] = "whitelist" };
        if (autostart != null)
            change["autostart"] = autostart.Value;
        foreach (var (k, v) in opts)
            change[k] = R.Arr(v);
        var sopt = Engines.StrategyOption(cfg.Engine);
        var sid = cfg.CurrentStrategy();
        var itype = Engines.ItemType(cfg.Engine);
        var dstrat = PresetLogic.DefaultStrategy(p, cfg.Engine);
        if ((sid.Length == 0 || idx.Get(itype, sid) == null) && dstrat != null && idx.Get(itype, dstrat) != null)
            change[sopt] = dstrat;
        var ncfg = c.Config.Preview("main", change);
        var (fail, warnings) = await ValidateChangeAsync(cfg, ncfg, ct).ConfigureAwait(false);
        if (fail != null)
            return fail;
        if (!c.Config.Set("main", change))
            return R.Fail("write_failed", T.S("svc.save_settings_failed"));
        foreach (var n in enableSources)
            if (!srcs[n].Enabled)
                c.Config.SetSource(n, new JsonObject { ["enabled"] = true });
        foreach (var n in disable)
            c.Config.SetSource(n, new JsonObject { ["enabled"] = false });
        var (reloaded, _) = await engine.ReloadIfNeededAsync(c.Config.Load(), ct).ConfigureAwait(false);
        var variants = new JsonObject();
        foreach (var id in selected)
            variants[id] = chosen[id].Id;
        var res = R.Ok(new JsonObject
        {
            ["services"] = R.Arr(selected), ["variants"] = variants, ["skipped"] = skipped, ["lists"] = R.Arr(opts["lists"]),
            ["ipsets"] = R.Arr(opts["ipsets"]), ["exclude_lists"] = R.Arr(opts["exclude_lists"]), ["exclude_ipsets"] = R.Arr(opts["exclude_ipsets"]),
            ["list_mode"] = "whitelist", ["strategy"] = ncfg.CurrentStrategy(), ["sources"] = R.Arr(enableSources),
            ["sources_disabled"] = R.Arr(disable), ["reloaded"] = reloaded, ["warnings"] = R.Arr(warnings),
        });
        // an error of the side job is not a configuration warning (router contract v1.2 §6.2)
        if (enableSources.Count > 0)
        {
            var j = c.Jobs.Start("sources-update", ctx => SourcesUpdateJobAsync(enableSources, ctx, false));
            if (R.IsOk(j))
                res["job"] = j["job"]!.DeepClone();
            else
                res["job_error"] = new JsonObject { ["code"] = R.Error(j), ["message"] = R.Message(j) };
        }
        return res;
    }
}
