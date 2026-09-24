using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;

namespace Zaprett.Ui.Core.DevFakes;

public sealed partial class FakeZaprettClient
{
    private static readonly string[] TestCandidates =
    [
        "strategy-general", "strategy-alt", "strategy-alt2", "strategy-alt3", "strategy-alt4", "strategy-alt8", "strategy-alt11",
        "strategy-fake-tls-auto-alt", "strategy-fake-tls-auto-alt3", "strategy-simple-fake", "strategy-discord-fix", "strategy-youtubefix-alt",
    ];

    private const string GeneralText =
        "--filter-udp=443 ${hostlists} --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=${bin:quic_initial_www_google_com} --new\n" +
        "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-repeats=6 --new\n" +
        "--filter-tcp=80 ${hostlists} --dpi-desync=fake,multisplit --dpi-desync-autottl=2 --dpi-desync-fooling=md5sig --new\n" +
        "--filter-tcp=443 ${hostlists} --dpi-desync=fake,multidisorder --dpi-desync-split-pos=midsld --dpi-desync-repeats=8 --dpi-desync-fooling=md5sig,badseq --new\n" +
        "--filter-udp=443 ${ipsets} --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=${bin:quic_initial_www_google_com}\n";

    private static readonly (string Id, string Type, string Name, string NameEn, string NameZh, int Entries)[] BundleLists =
    [
        ("zaprett-youtube", "list", "YouTube", "YouTube", "YouTube", 43),
        ("zaprett-youtube-full", "list", "YouTube (расширенный)", "YouTube (extended)", "YouTube（扩展）", 112),
        ("zaprett-discord", "list", "Discord", "Discord", "Discord", 31),
        ("zaprett-discord-full", "list", "Discord (расширенный)", "Discord (extended)", "Discord（扩展）", 78),
        ("zaprett-telegram", "list", "Telegram", "Telegram", "Telegram", 19),
        ("zaprett-rutracker", "list", "RuTracker", "RuTracker", "RuTracker", 6),
        ("zaprett-rutracker-full", "list", "RuTracker (расширенный)", "RuTracker (extended)", "RuTracker（扩展）", 14),
        ("zaprett-roblox", "list", "Roblox", "Roblox", "Roblox", 22),
        ("zaprett-roblox-full", "list", "Roblox (расширенный)", "Roblox (extended)", "Roblox（扩展）", 64),
        ("zaprett-signal", "list", "Signal", "Signal", "Signal", 9),
        ("zaprett-signal-full", "list", "Signal (расширенный)", "Signal (extended)", "Signal（扩展）", 21),
        ("zaprett-exclude", "list_exclude", "Исключения: сайты", "Exclusions: websites", "排除项：网站", 412),
        ("zaprett-discord-voice", "ipset", "Discord: голосовые серверы (IP)", "Discord: voice servers (IP)", "Discord：语音服务器（IP）", 57),
        ("zaprett-telegram-ipset", "ipset", "Telegram: IP-сети", "Telegram: IP networks", "Telegram：IP 网段", 12),
        ("zaprett-roblox-ipset", "ipset", "Roblox: IP-сети", "Roblox: IP networks", "Roblox：IP 网段", 18),
        ("zaprett-cloudflare-ipset", "ipset", "Cloudflare: IP-сети (IPv4)", "Cloudflare: IP networks (IPv4)", "Cloudflare：IP 网段（IPv4）", 15),
        ("zaprett-cloudflare-ipset6", "ipset", "Cloudflare: IP-сети (IPv6)", "Cloudflare: IP networks (IPv6)", "Cloudflare：IP 网段（IPv6）", 7),
        ("zaprett-exclude-ipset", "ipset_exclude", "Исключения: локальные сети", "Exclusions: local networks", "排除项：局域网", 16),
    ];

    // ---------- presets and wizard ----------

    private static IEnumerable<JsonObject> Sets(JsonObject s)
    {
        yield return new JsonObject { ["id"] = null, ["lists"] = s.Arr("lists").DeepClone(), ["ipsets"] = s.Arr("ipsets").DeepClone(), ["sources"] = s.Arr("sources").DeepClone() };
        foreach (var v in s.Objs("variants"))
            yield return v;
    }

    private bool SetOn(JsonObject set, out int size)
    {
        var items = set.Strings("lists").Concat(set.Strings("ipsets")).ToList();
        var sources = set.Strings("sources");
        size = items.Count + sources.Count;
        return size > 0 && items.All(_enabledItems.Contains) && sources.All(n => _sources.Obj(n).Bool("enabled"));
    }

    private JsonObject BuildPresets()
    {
        var p = _presetsFile.Clone();
        foreach (var s in p.Objs("services").ToList())
        {
            var core = Sets(s).First();
            s["enabled"] = SetOn(core, out var size);
            s["partially_enabled"] = !s.Bool("enabled") && core.Strings("lists").Concat(core.Strings("ipsets")).Any(_enabledItems.Contains);
            s["available"] = size > 0 && s.Str("works") != "no";
            string? enabledVariant = null;
            foreach (var v in s.Objs("variants"))
            {
                v["enabled"] = SetOn(v, out var vs);
                v["available"] = vs > 0 && s.Str("works") != "no";
                if (v.Bool("enabled") && !s.Bool("enabled"))
                    enabledVariant = v.Str("id");
            }
            s["enabled_variant"] = enabledVariant;
            if (enabledVariant != null)
                s["enabled"] = false;
        }
        p["ok"] = true;
        p["ram_total_mib"] = 16384;
        p["recommended_tier"] = "full";
        return p;
    }

    private JsonObject WizardApply(List<string> refs)
    {
        if (refs.Count == 0)
            return Fail("bad_args", M("Укажите хотя бы один сервис", "Give at least one service", "请至少指定一个服务"));
        var services = _presetsFile.Objs("services").ToDictionary(s => s.Str("id") ?? "", s => s);
        var chosen = new Dictionary<string, string?>();
        var skipped = new JsonArray();
        foreach (var r in refs)
        {
            var parts = r.Split(':', 2);
            if (!services.TryGetValue(parts[0], out var svc))
                return Fail("unknown_service", M($"Сервис «{parts[0]}» не найден в пресетах", $"Service '{parts[0]}' is not in the presets", $"预设中没有服务“{parts[0]}”"));
            var variant = parts.Length > 1 ? parts[1] : null;
            if (variant != null && svc.Objs("variants").All(v => v.Str("id") != variant))
                return Fail("unknown_variant", M($"У сервиса «{parts[0]}» нет варианта «{variant}»", $"Service '{parts[0]}' has no list set '{variant}'", $"服务“{parts[0]}”没有列表集“{variant}”"));
            if (chosen.TryGetValue(parts[0], out var prev) && prev != variant)
                return Fail("bad_args", M($"Сервис «{parts[0]}» указан с разными вариантами", $"Service '{parts[0]}' is given with different list sets", $"服务“{parts[0]}”被指定了不同的列表集"));
            if (svc.Str("works") == "no")
            {
                skipped.Add(new JsonObject { ["id"] = parts[0], ["reason"] = "works_no" });
                continue;
            }
            chosen[parts[0]] = variant;
        }
        if (chosen.Count == 0)
            return Fail("preset_unavailable", M("Выбранные сервисы нельзя включить: обход для них не работает", "The chosen services cannot be enabled: the bypass does not work for them", "所选服务无法开启：绕过对它们无效"), new() { ["skipped"] = skipped });

        foreach (var svc in services.Values)
            foreach (var set in Sets(svc))
                foreach (var id in set.Strings("lists").Concat(set.Strings("ipsets")))
                    _enabledItems.Remove(id);
        var sources = new List<string>();
        var disabledSources = new List<string>();
        foreach (var src in _sources.Select(kv => kv.Key).ToList())
            if (_sources.Obj(src).Bool("enabled"))
            {
                _sources[src]!["enabled"] = false;
                disabledSources.Add(src);
            }
        foreach (var (id, variant) in chosen)
        {
            var set = variant == null ? Sets(services[id]).First() : services[id].Objs("variants").First(v => v.Str("id") == variant);
            _enabledItems.UnionWith(set.Strings("lists").Concat(set.Strings("ipsets")));
            foreach (var src in set.Strings("sources"))
            {
                if (_sources.Obj(src) is { } so)
                    so["enabled"] = true;
                else
                    _sources[src] = new JsonObject { ["name"] = src, ["enabled"] = true, ["title"] = src, ["type"] = "ipset", ["url"] = "https://example.invalid/" + src, ["interval_hours"] = 168, ["last_update"] = 0, ["entries"] = 0, ["size"] = 0, ["status"] = "never", ["error"] = null, ["ram_mib"] = 0, ["item_id"] = "src-" + src };
                sources.Add(src);
                disabledSources.Remove(src);
            }
        }
        _config["main"]!["list_mode"] = "whitelist";
        var variants = new JsonObject();
        foreach (var (id, variant) in chosen)
            variants[id] = variant;
        return Ok(new()
        {
            ["services"] = chosen.Keys.ToJsonArray(), ["variants"] = variants, ["skipped"] = skipped,
            ["lists"] = ActiveIncludes().ToJsonArray(), ["sources"] = sources.ToJsonArray(),
            ["sources_disabled"] = disabledSources.ToJsonArray(), ["reloaded"] = Running, ["warnings"] = new JsonArray(),
        });
    }

    // ---------- items, lists, strategies ----------

    private JsonObject BuildItems(string? type)
    {
        var items = new JsonArray();
        var engineType = _config.Obj("main").Str("engine") == "winws2" ? "nfqws2" : "nfqws";
        foreach (var s in TestCandidates.Concat(["strategy-alt2-roblox", "strategy-general-mgts", "strategy-shizapret-port"]).Distinct())
            items.Add(new JsonObject
            {
                ["id"] = s, ["type"] = "nfqws", ["name"] = s, ["version"] = "1.0.0", ["author"] = "flowseal", ["source"] = "bundle",
                ["description"] = "Стратегия из набора zapret для Windows", ["description_en"] = "Strategy from the zapret set for Windows",
                ["description_zh"] = "来自 Windows 版 zapret 的策略", ["entries"] = null, ["size"] = 900 + s.Length * 13,
                ["active"] = engineType == "nfqws" && s == StrategyId, ["used_by"] = new JsonArray(),
            });
        foreach (var (id, text) in _userStrategies)
            items.Add(new JsonObject
            {
                ["id"] = id, ["type"] = "nfqws", ["name"] = id, ["version"] = "", ["author"] = "", ["source"] = "user",
                ["description"] = "", ["size"] = text.Length, ["active"] = id == StrategyId, ["used_by"] = new JsonArray(),
            });
        foreach (var z2 in new[] { "z2-general", "z2-alt", "z2-alt2", "z2-hostfakesplit" })
            items.Add(new JsonObject
            {
                ["id"] = z2, ["type"] = "nfqws2", ["name"] = z2, ["version"] = "1.0.0", ["author"] = "zaprett-openwrt", ["source"] = "bundle",
                ["description"] = "Стратегия движка zapret2", ["description_en"] = "Strategy of the zapret2 engine", ["description_zh"] = "zapret2 引擎的策略", ["size"] = 700, ["active"] = engineType == "nfqws2" && z2 == "z2-general",
                ["used_by"] = new JsonArray(),
            });
        foreach (var (id, t, name, nameEn, nameZh, entries) in BundleLists)
            items.Add(new JsonObject
            {
                ["id"] = id, ["type"] = t, ["name"] = name, ["name_en"] = nameEn, ["name_zh"] = nameZh, ["version"] = "2026.09.22", ["author"] = "zaprett-openwrt",
                ["source"] = "bundle", ["description"] = "", ["entries"] = entries, ["size"] = entries * 18, ["active"] = _enabledItems.Contains(id),
                ["used_by"] = new JsonArray(),
            });
        foreach (var (id, t) in new[] { ("user-hosts", "list"), ("user-hosts-exclude", "list_exclude"), ("user-ipset", "ipset"), ("user-ipset-exclude", "ipset_exclude") })
        {
            var text = _userTexts.GetValueOrDefault(id, "");
            items.Add(new JsonObject
            {
                ["id"] = id, ["type"] = t, ["name"] = id, ["source"] = "user", ["entries"] = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                ["size"] = text.Length, ["active"] = _enabledItems.Contains(id), ["used_by"] = new JsonArray(),
            });
        }
        foreach (var (name, src) in _sources)
            items.Add(new JsonObject
            {
                ["id"] = src.Str("item_id"), ["type"] = src.Str("type"), ["name"] = src.Str("title"), ["source"] = "url",
                ["entries"] = src.Long("entries"), ["size"] = src.Long("size"), ["active"] = src.Bool("enabled"), ["used_by"] = new JsonArray(),
            });
        foreach (var item in items.OfType<JsonObject>())
            ApplyManifestTexts(item);
        if (type != null)
            items = new JsonArray(items.OfType<JsonObject>().Where(i => i.Str("type") == type).Select(i => (JsonNode?)i.DeepClone()).ToArray());
        return Ok(new() { ["items"] = items });
    }

    private JsonObject ToggleItem(string? id, bool on)
    {
        if (id == null || !(BundleLists.Any(b => b.Id == id) || _userTexts.ContainsKey(id)))
            return Fail("not_found", M("Список не найден", "The list was not found", "未找到该列表"));
        if (on)
            _enabledItems.Add(id);
        else
            _enabledItems.Remove(id);
        return Ok(new() { ["id"] = id, ["active"] = on, ["reloaded"] = Running });
    }

    private JsonObject SetStrategy(string? id)
    {
        if (id == null || !(TestCandidates.Contains(id) || _userStrategies.ContainsKey(id) || id.StartsWith("strategy-", StringComparison.Ordinal) || id.StartsWith("z2-", StringComparison.Ordinal)))
            return Fail("strategy_not_found", M("Стратегия не найдена", "The strategy was not found", "未找到该策略"));
        StrategyId = id;
        _degraded = false;
        _monitor["state"] = "ok";
        _monitor["consecutive_failures"] = 0;
        if (_probe != null)
            _probe = BuildProbe(_probe.Long("finished"));
        _config["main"]!["strategy"] = id;
        var ev = StatusEvent();
        _ = Task.Run(() => Publish("status", ev));
        return Ok(new() { ["strategy"] = id, ["engine"] = _config.Obj("main").Str("engine"), ["reloaded"] = Running, ["warnings"] = new JsonArray() });
    }

    private JsonObject ShowStrategy(string? id)
    {
        if (id == null)
            return Fail("not_found", M("Стратегия не найдена", "The strategy was not found", "未找到该策略"));
        var isUser = _userStrategies.TryGetValue(id, out var text);
        text ??= GeneralText;
        return Ok(new()
        {
            ["id"] = id, ["type"] = id.StartsWith("z2-", StringComparison.Ordinal) ? "nfqws2" : "nfqws", ["name"] = id,
            ["source"] = isUser ? "user" : "bundle", ["description"] = isUser ? "" : M("Стратегия из набора zapret для Windows", "Strategy from the zapret set for Windows", "来自 Windows 版 zapret 的策略"),
            ["dependencies"] = new JsonArray("quic_initial_www_google_com"), ["text"] = text,
            ["args"] = new JsonArray("--filter-udp=443", "--dpi-desync=fake", "--new", "--filter-tcp=443", "--dpi-desync=fake,multidisorder"),
            ["ports"] = new JsonObject { ["tcp"] = new JsonArray(80, 443), ["udp"] = new JsonArray(443, "19294-19344", "50000-50100") },
            ["warnings"] = new JsonArray(),
        });
    }

    private JsonObject SaveStrategy(string? id, string? text)
    {
        if (id == null || !id.StartsWith("user-", StringComparison.Ordinal) || id.Length < 6 || !Zaprett.Ui.Core.ViewModels.Validation.IsId(id))
            return Fail("bad_id", "Имя своей стратегии должно начинаться с «user-» и содержать только латиницу, цифры, «.», «_», «-»");
        if (text == null || text.Length > 64 * 1024)
            return Fail("too_large", M("Текст стратегии больше 64 КиБ", "The strategy text is larger than 64 KiB", "策略文本超过 64 KiB"));
        var bad = text.Split('\n').SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(t => t.StartsWith("--", StringComparison.Ordinal) && !KnownOption(t));
        if (bad != null)
            return Fail("dry_run_failed", M($"Движок не принял аргументы: неизвестный параметр {bad}", $"The engine rejected the arguments: unknown option {bad}", $"引擎拒绝了参数：未知选项 {bad}"), new()
            {
                ["dry_run"] = new JsonObject { ["rc"] = 1, ["output"] = $"winws: unrecognized option '{bad.Split('=')[0]}'" },
            });
        _userStrategies[id] = text;
        return Ok(new()
        {
            ["id"] = id, ["engine"] = "winws",
            ["args"] = new JsonArray("--wf-tcp=443", "--filter-tcp=443", "--dpi-desync=fake,multidisorder"),
            ["ports"] = new JsonObject { ["tcp"] = new JsonArray(443), ["udp"] = new JsonArray() },
            ["warnings"] = new JsonArray(), ["reloaded"] = id == StrategyId && Running,
        });
    }

    private static bool KnownOption(string token)
    {
        var name = token.Split('=')[0];
        return name is "--filter-tcp" or "--filter-udp" or "--filter-l7" or "--new" or "--comment" or "--hostlist" or "--hostlist-domains"
            or "--ipset" or "--hostlist-exclude" or "--ipset-exclude" || name.StartsWith("--dpi-desync", StringComparison.Ordinal)
            || name.StartsWith("--wf-", StringComparison.Ordinal);
    }

    private JsonObject DeleteStrategy(string? id)
    {
        if (id == null || !id.StartsWith("user-", StringComparison.Ordinal))
            return Fail("bad_id", "Удалять можно только свои стратегии (с префиксом «user-»)");
        if (id == StrategyId)
            return Fail("item_active", M($"Стратегия «{id}» сейчас выбрана. Сначала выберите другую.", $"Strategy '{id}' is selected now. Choose another one first.", $"策略“{id}”正在使用，请先选择其他策略。"));
        return _userStrategies.Remove(id) ? Ok(new() { ["id"] = id }) : Fail("not_found", M($"Своя стратегия «{id}» не найдена", $"Own strategy '{id}' was not found", $"未找到自定义策略“{id}”"));
    }

    private JsonObject SetUserList(string? id, string text)
    {
        if (id == null || !_userTexts.ContainsKey(id))
            return Fail("not_found", M("Список не найден", "The list was not found", "未找到该列表"));
        var isIp = id.Contains("ipset", StringComparison.Ordinal);
        var errors = new JsonArray();
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith('#'))
                continue;
            var valid = isIp ? System.Net.IPNetwork.TryParse(l, out _) || System.Net.IPAddress.TryParse(l, out _)
                : Zaprett.Ui.Core.ViewModels.Validation.IsDomain(l);
            if (!valid)
                errors.Add(new JsonObject { ["line"] = i + 1, ["value"] = l, ["reason"] = isIp ? "bad_cidr" : "bad_domain" });
        }
        if (errors.Count > 0)
            return Fail("invalid_entries", M("Некоторые строки неверны", "Some lines are not valid", "部分行无效"), new() { ["errors"] = errors });
        _userTexts[id] = text;
        return Ok(new() { ["id"] = id, ["entries"] = lines.Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')), ["reloaded"] = Running });
    }

    private JsonObject SetConfig(string section, string key, string? value)
    {
        if (value == null)
            return Fail("bad_value", M("Недопустимое значение", "Invalid value", "无效的值"));
        _config[section]![key] = value;
        return Ok(new() { ["reloaded"] = Running });
    }

    private JsonObject SettingsSet(JsonObject patch)
    {
        var interval = patch.Obj("monitor")?.Get("interval");
        if (interval != null && patch.Obj("monitor").Int("interval") is not (10 or 15 or 20 or 30 or 60))
            return Fail("bad_value", M("Интервал монитора: 10, 15, 20, 30 или 60 минут", "Monitor interval: 10, 15, 20, 30 or 60 minutes", "监控间隔：10、15、20、30 或 60 分钟"), new() { ["details"] = new JsonObject { ["bad_options"] = new JsonArray("monitor.interval") } });
        Merge(_config, patch);
        return Ok(new() { ["settings"] = _config.Clone(), ["reloaded"] = Running });
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (k, v) in patch)
        {
            if (k == "schema")
                continue;
            if (v is JsonObject po && target[k] is JsonObject to && k != "sources")
                Merge(to, po);
            else
                target[k] = v?.DeepClone();
        }
    }

    private JsonObject BuildConflicts()
    {
        var items = new JsonArray();
        if (Scenario == Scenarios.Conflicts)
        {
            items.Add(new JsonObject
            {
                ["id"] = "goodbyedpi", ["name"] = "GoodbyeDPI", ["severity"] = "block", ["kind"] = "service", ["service"] = "GoodbyeDPI",
                ["path"] = @"C:\Tools\goodbyedpi\x86_64\goodbyedpi.exe", ["pid"] = 3120,
                // as the service sends them: English, the interface uses its own texts for this finding
                ["detail"] = "Service \"GoodbyeDPI\" (GoodbyeDPI) is running: two DPI bypass programs on one PC break each other",
                ["fix"] = "Stop and disable the service \"GoodbyeDPI\" (services.msc).",
            });
            items.Add(new JsonObject
            {
                ["id"] = "adguard", ["name"] = "AdGuard", ["severity"] = "warn", ["kind"] = "service", ["service"] = "AdGuard Service",
                ["path"] = @"C:\Program Files\AdGuard\AdguardSvc.exe", ["pid"] = 2244,
                ["detail"] = "Service \"AdGuard Service\" (AdGuard Service) is running: AdGuard filters traffic with its own driver",
                ["fix"] = "Stop and disable the service \"AdGuard Service\" (services.msc).",
            });
        }
        items.Add(new JsonObject
        {
            ["id"] = "vpn", ["name"] = "WireGuard", ["severity"] = "info",
            ["detail"] = M("Найден VPN-адаптер WireGuard (сейчас не подключён).",
                "A WireGuard VPN adapter was found (not connected now).",
                "发现 WireGuard VPN 网卡（当前未连接）。"),
            ["fix"] = M("Пока VPN подключён, трафик идёт через него и обход не нужен; ничего делать не надо.",
                "While the VPN is connected, the traffic goes through it and the bypass is not needed; nothing to do.",
                "VPN 连接时流量经由 VPN，无需绕过；无需任何操作。"),
        });
        return Ok(new() { ["items"] = items });
    }

    // ---------- probe, monitor, diagnose, test ----------

    private JsonObject BuildProbe(long finished)
    {
        var services = new JsonArray();
        var bad = _degraded || !Running;
        services.Add(ProbeService("youtube", "YouTube", bad ? 0 : 3, 3, bad ? 0 : 420, ["https://www.youtube.com/", "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", "https://redirector.googlevideo.com/report_mapping?di=no"], bad ? "timeout" : null));
        services.Add(ProbeService("discord", "Discord", !Running ? 1 : 2, 2, 180, ["https://discord.com/", "https://gateway.discord.gg/"], !Running ? "reset" : null));
        if (_enabledItems.Contains("zaprett-rutracker"))
            services.Add(ProbeService("rutracker", "RuTracker", !Running ? 0 : 1, 1, 610, ["https://rutracker.org/forum/index.php"], !Running ? "tls_cert" : null));
        return new JsonObject
        {
            ["started"] = finished - 6, ["finished"] = finished, ["engine_running"] = Running, ["strategy"] = StrategyId,
            ["ok"] = services.OfType<JsonObject>().Sum(s => s.Int("ok")), ["total"] = services.OfType<JsonObject>().Sum(s => s.Int("total")),
            ["services"] = services,
        };
    }

    private static JsonObject ProbeService(string id, string name, int ok, int total, int ms, string[] urls, string? error)
    {
        var targets = new JsonArray();
        for (var i = 0; i < urls.Length; i++)
        {
            var good = i < ok;
            targets.Add(new JsonObject
            {
                ["url"] = urls[i], ["ok"] = good, ["ms"] = good ? ms + i * 37 : 8000, ["bytes"] = good ? 131072 + i * 4096 : 0,
                ["error"] = good ? null : error ?? "failed",
            });
        }
        return new JsonObject { ["id"] = id, ["name"] = name, ["ok"] = ok, ["total"] = total, ["avg_ms"] = ok > 0 ? ms : 0, ["targets"] = targets };
    }

    private JsonObject? FinishProbe(JsonObject job)
    {
        _probe = BuildProbe(Now());
        return new JsonObject { ["reachable"] = _probe.Int("ok"), ["total"] = _probe.Int("total") };
    }

    private JsonObject BuildMonitor(long now)
    {
        var rng = new Random(42);
        var history = new JsonArray();
        for (var i = 47; i >= 0; i--)
        {
            var t = now - (i + 1) * 30 * 60;
            var fail = _degraded ? i < 3 || rng.Next(12) == 0 : rng.Next(16) == 0;
            var total = i == 30 ? 0 : 5;
            history.Add(new JsonObject { ["t"] = t, ["ok"] = total == 0 ? 0 : fail ? 1 : 5, ["total"] = total });
        }
        return new JsonObject
        {
            ["enabled"] = true, ["auto_repair"] = _degraded, ["interval"] = 30, ["threshold"] = 3,
            ["state"] = Scenario == Scenarios.FirstRun ? "unknown" : _degraded ? "degraded" : "ok",
            ["checked_at"] = now - 30 * 60, ["ok"] = _degraded ? 1 : 5, ["total"] = 5, ["consecutive_failures"] = _degraded ? 3 : 0,
            ["history"] = Scenario == Scenarios.FirstRun ? new JsonArray() : history,
            ["last_repair"] = _degraded ? new JsonObject { ["t"] = now - 26 * 3600, ["job_id"] = "1790000000-1111" } : null,
        };
    }

    private JsonObject? FinishDiagnose(JsonObject job)
    {
        var targets = new JsonArray
        {
            DiagTarget("https://www.youtube.com/", "www.youtube.com", Running ? "ok" : "throttle", ["142.250.74.46"], ["142.250.74.46"], false, Running ? "" : M("данные остановились на 16 384 байтах", "the data stopped at 16,384 bytes", "数据在 16384 字节处中断")),
            DiagTarget("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", "i.ytimg.com", Running ? "ok" : "throttle", ["142.250.74.54"], ["142.250.74.54"], false, ""),
            DiagTarget("https://discord.com/", "discord.com", Running ? "ok" : "tls_block", ["162.159.137.232"], ["162.159.137.232", "162.159.138.232"], false, Running ? "" : M("соединение сброшено после ClientHello", "the connection was reset after ClientHello", "ClientHello 之后连接被重置")),
            DiagTarget("https://rutracker.org/forum/index.php", "rutracker.org", "dns_spoof", ["195.208.4.1"], ["104.21.32.39", "172.67.182.196"], true, M("системный DNS вернул адрес-заглушку провайдера", "the system DNS returned a stub address of the provider", "系统 DNS 返回了运营商的拦截地址")),
        };
        var counts = new JsonObject();
        foreach (var t in targets.OfType<JsonObject>())
            counts[t.Str("verdict")!] = counts.Int(t.Str("verdict")!) + 1;
        var verdict = targets.OfType<JsonObject>().Select(t => t.Str("verdict")!).Where(v => v != "ok")
            .GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => Array.IndexOf(Text.UiText.VerdictOrder, g.Key)).Select(g => g.Key).FirstOrDefault() ?? "ok";
        _diagnose = new JsonObject
        {
            ["started"] = Now() - 8, ["finished"] = Now(), ["engine_running"] = Running, ["targets"] = targets,
            ["summary"] = new JsonObject { ["verdict"] = verdict, ["counts"] = counts },
        };
        return new JsonObject { ["verdict"] = verdict };
    }

    private JsonObject DiagTarget(string url, string host, string verdict, string[] sys, string[] doh, bool spoofed, string detail) => new()
    {
        ["url"] = url, ["host"] = host, ["verdict"] = verdict, ["detail"] = detail,
        ["dns"] = new JsonObject { ["system"] = sys.ToJsonArray(), ["doh"] = doh.ToJsonArray(), ["spoofed"] = spoofed },
    };

    private bool _testApplyIfBetter;

    private JsonObject StartTest(JsonObject a)
    {
        _testApplyIfBetter = a.Bool("apply_if_better");
        var res = StartJob("test", 12, FinishTest);
        if (res.IsOk())
            _testResults = new JsonObject
            {
                ["started"] = Now(), ["finished"] = 0, ["state"] = "running", ["mode"] = a.Bool("exclusive") ? "exclusive" : "isolated",
                ["mode_reason"] = a.Bool("exclusive") ? "forced" : null, ["results"] = new JsonArray(),
            };
        return res;
    }

    private JsonObject? FinishTest(JsonObject job)
    {
        var results = new JsonArray();
        var rng = new Random(7);
        var i = 0;
        foreach (var id in TestCandidates)
        {
            var total = 6;
            var ok = id switch
            {
                "strategy-alt3" => 6,
                "strategy-fake-tls-auto-alt" => 6,
                "strategy-general" => _degraded ? 2 : 5,
                "strategy-youtubefix-alt" => 0,
                _ => rng.Next(1, 6),
            };
            var status = id == "strategy-youtubefix-alt" ? "invalid" : "done";
            results.Add(new JsonObject
            {
                ["id"] = id, ["name"] = id, ["ok"] = status == "done" ? ok : 0, ["total"] = total,
                ["ratio"] = status == "done" ? Math.Round((double)ok / total, 3) : 0, ["avg_ms"] = 300 + (i++ * 53 % 400), ["status"] = status,
            });
        }
        var sorted = results.OfType<JsonObject>().OrderByDescending(r => r.Get("ratio")!.GetValue<double>()).ThenBy(r => r.Long("avg_ms"))
            .Select(r => (JsonNode?)r.DeepClone()).ToArray();
        _testResults ??= new JsonObject();
        _testResults["finished"] = Now();
        _testResults["state"] = "done";
        _testResults["baseline"] = new JsonObject { ["ok"] = 1, ["total"] = 6 };
        _testResults["results"] = new JsonArray(sorted);
        // --apply-if-better (router contract §14.3): a strictly better strategy than the current one is applied
        var best = sorted[0]!.Str("id");
        var currentRatio = sorted.Select(r => r!.AsObject()).FirstOrDefault(r => r.Str("id") == StrategyId)?.Get("ratio")?.GetValue<double>() ?? 0;
        string? applied = null;
        if (_testApplyIfBetter && best != null && best != StrategyId && sorted[0]!.Get("ratio")!.GetValue<double>() > currentRatio)
        {
            SetStrategy(best);
            applied = best;
        }
        _testResults["applied"] = applied;
        return new JsonObject { ["tested"] = results.Count, ["best"] = best, ["baseline_ok"] = 1, ["applied"] = applied, ["mode"] = _testResults.Str("mode") };
    }

    private JsonObject BuildTestStatus()
    {
        var running = _job?.Str("name") == "test" && _job.Str("state") == "running";
        var results = _testResults?.Clone();
        if (Scenario == Scenarios.Testing && results == null)
            results = new JsonObject
            {
                ["started"] = Now() - 90, ["finished"] = 0, ["state"] = "running", ["mode"] = "isolated",
                ["baseline"] = new JsonObject { ["ok"] = 1, ["total"] = 6 },
                ["results"] = new JsonArray(
                    new JsonObject { ["id"] = "strategy-alt3", ["name"] = "strategy-alt3", ["ok"] = 6, ["total"] = 6, ["ratio"] = 1.0, ["avg_ms"] = 390, ["status"] = "done" },
                    new JsonObject { ["id"] = "strategy-general", ["name"] = "strategy-general", ["ok"] = 5, ["total"] = 6, ["ratio"] = 0.833, ["avg_ms"] = 410, ["status"] = "done" },
                    new JsonObject { ["id"] = "strategy-alt", ["name"] = "strategy-alt", ["ok"] = 3, ["total"] = 6, ["ratio"] = 0.5, ["avg_ms"] = 520, ["status"] = "done" },
                    new JsonObject { ["id"] = "strategy-alt2", ["name"] = "strategy-alt2", ["ok"] = 0, ["total"] = 6, ["ratio"] = 0.0, ["avg_ms"] = 0, ["status"] = "invalid" }),
            };
        return Ok(new()
        {
            ["job"] = _job?.Str("name") == "test" ? _job.Clone() : null, ["running"] = running,
            ["mode"] = results?.Str("mode"), ["mode_reason"] = results?.Str("mode_reason"), ["results"] = results,
        });
    }

    private JsonObject SetDns(bool enable)
    {
        var mode = enable ? "doh" : "system";
        var changed = _config.Obj("dns").Str("mode") != mode;
        _config["dns"]!["mode"] = mode;
        return Ok(new() { ["changed"] = changed });
    }

    private JsonObject SaveSource(JsonObject a)
    {
        var name = a.Str("name");
        var url = a.Str("url");
        if (name == null || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9_]{1,32}$"))
            return Fail("bad_value", M("Имя подписки: латиница в нижнем регистре, цифры и «_», до 32 символов", "Subscription name: lower-case Latin letters, digits and '_', up to 32 characters", "订阅名称：小写拉丁字母、数字和“_”，最多 32 个字符"));
        if (url == null || !url.StartsWith("https://", StringComparison.Ordinal))
            return Fail("bad_value", M("Адрес подписки должен начинаться с https://", "The subscription address must start with https://", "订阅地址必须以 https:// 开头"));
        _sources[name] = new JsonObject
        {
            ["name"] = name, ["enabled"] = a.Bool("enabled", true), ["title"] = a.Str("title") ?? name, ["type"] = a.Str("type") ?? "list",
            ["url"] = url, ["interval_hours"] = a.Int("interval_hours", 72), ["last_update"] = 0, ["entries"] = 0, ["size"] = 0,
            ["status"] = "never", ["error"] = null, ["ram_mib"] = 0, ["item_id"] = "src-" + name,
        };
        return Ok(new() { ["name"] = name });
    }

    private JsonObject? FinishSources(JsonObject job)
    {
        foreach (var (_, s) in _sources)
            if (s is JsonObject so && so.Bool("enabled"))
            {
                so["last_update"] = Now();
                so["status"] = "ok";
                so["error"] = null;
                if (so.Long("entries") == 0)
                    so["entries"] = 22;
            }
        return new JsonObject();
    }

    private List<string> FakeLog()
    {
        var t = DateTimeOffset.Now.AddMinutes(-40);
        string At(int min) => t.AddMinutes(min).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return
        [
            $"{At(0)} zaprett: " + M("служба запущена, версия " + ProductVersion, "service started, version " + ProductVersion, "服务已启动，版本 " + ProductVersion),
            $"{At(0)} zaprett: " + M("движок winws v72.13 запущен, pid 7412, стратегия strategy-general", "engine winws v72.13 started, pid 7412, strategy strategy-general", "引擎 winws v72.13 已启动，pid 7412，策略 strategy-general"),
            $"{At(0)} winws: windivert initialized. capture is started.",
            $"{At(10)} zaprett: " + M("монитор: открылось 5 из 5", "monitor: 5 of 5 opened", "监控：5/5 可访问"),
            $"{At(20)} zaprett: " + M("монитор: открылось 5 из 5", "monitor: 5 of 5 opened", "监控：5/5 可访问"),
            $"{At(30)} zaprett: " + M("проверка сайтов: YouTube 3/3, Discord 2/2, RuTracker 1/1", "site check: YouTube 3/3, Discord 2/2, RuTracker 1/1", "网站检查：YouTube 3/3、Discord 2/2、RuTracker 1/1"),
            $"{At(35)} zaprett: " + M("список user-hosts сохранён, 2 записи", "list user-hosts saved, 2 entries", "列表 user-hosts 已保存，2 条"),
        ];
    }

    private static string FakeDiag() =>
        "zaprett " + ProductVersion + " (fake mode of the interface)\nWindows 11 Pro 26100 x64\nwinws v72.13, WinDivert 2.2.2\n" +
        "config: engine=winws strategy=strategy-general list_mode=whitelist\nrepo.url: https://raw.githubusercontent.com/…/index.json\n";
}
