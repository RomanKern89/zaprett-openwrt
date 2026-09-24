using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Zaprett.Ipc;
using Zaprett.Ui.Core.Json;

namespace Zaprett.Ui.Core.DevFakes;

/// <summary>
/// Development stand-in for the zaprett service: answers the IPC methods the interface uses with realistic,
/// router-compatible JSON (router contract §6.2, §14–§17; ARCHITECTURE-WIN §6, §8) and simulates background jobs.
/// Presets are the real presets.json of the product (embedded at build time). Enabled with --fake[=scenario]
/// or ZAPRETT_UI_FAKE=1 (+ ZAPRETT_UI_FAKE_SCENARIO). Never used against a real service.
/// </summary>
public sealed partial class FakeZaprettClient : IZaprettClient
{
    public static class Scenarios
    {
        public const string Running = "running";
        public const string Stopped = "stopped";
        public const string FirstRun = "firstrun";
        public const string Unavailable = "unavailable";
        public const string Degraded = "degraded";
        public const string Testing = "testing";
        public const string Error = "error";
        public const string Conflicts = "conflicts";

        /// <summary>Bypass "running" while a foreign winws.exe works through the loaded WinDivert driver (status
        /// conflict_blocking, the case of the acceptance test).</summary>
        public const string Blocked = "blocked";

        /// <summary>Bypass "running", seen by a user who is neither an administrator nor in "zaprett Operators"
        /// (status.can_modify=false, every change answered with access_denied).</summary>
        public const string ReadOnly = "readonly";

        /// <summary>"running", but the first answers with the status come only after <see cref="SlowStartDelay"/>
        /// (a service still starting): pages open before the shared state has a status.</summary>
        public const string SlowStart = "slowstart";

        public static readonly string[] All = [Running, Stopped, FirstRun, Unavailable, Degraded, Testing, Error, Conflicts, Blocked, ReadOnly, SlowStart];
    }

    private readonly object _gate = new();
    private readonly List<Channel<ServiceEvent>> _subscribers = [];
    private readonly JsonObject _presetsFile;
    private readonly HashSet<string> _enabledItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _userTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _userStrategies = new(StringComparer.Ordinal);
    private readonly JsonObject _sources = [];
    private JsonObject _config;
    private JsonObject? _job;
    private JsonObject? _probe;
    private JsonObject? _diagnose;
    private JsonObject? _testResults;
    private JsonObject _monitor;
    private CancellationTokenSource? _jobCts;
    private long _startedAt;
    private bool _degraded;
    private string _lang = Loc.L.Russian;

    /// <summary>Text in the language of the current call ("lang" argument, ARCHITECTURE-WIN §12.2).</summary>
    private string M(string ru, string en, string zh) => _lang switch
    {
        Loc.L.English => en,
        Loc.L.Chinese => zh,
        _ => ru,
    };

    public FakeZaprettClient(string scenario = Scenarios.Running)
    {
        _presetsFile = LoadPresets();
        _config = DefaultConfig();
        _monitor = new JsonObject();
        SetScenario(scenario);
    }

    /// <summary>Scales the duration of simulated jobs (screenshots and tests use small values).</summary>
    public double JobSeconds { get; set; } = 1.0;

    public string Scenario { get; private set; } = Scenarios.Running;

    public bool Available { get; private set; } = true;

    public bool Enabled { get; private set; }

    public bool Running { get; private set; }

    public string StrategyId { get; private set; } = "strategy-general";

    /// <summary>
    /// The version the fake service reports: the one this build carries (Directory.Build.props, X.Y.Z without the
    /// "+commit" of SourceLink), so the screenshots of the settings and the log show the version they document.
    /// </summary>
    public static string ProductVersion { get; } =
        (typeof(FakeZaprettClient).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>How long the status waits in the scenario SlowStart.</summary>
    public TimeSpan SlowStartDelay { get; set; } = TimeSpan.FromSeconds(4);

    private DateTimeOffset _statusFrom = DateTimeOffset.MinValue;

    /// <summary>status.can_modify: the rights of the caller; false answers every change with access_denied.</summary>
    public bool CanModify { get; set; } = true;

    /// <summary>The HKLM Run value of the interface (status.tray_autostart, method tray.autostart).</summary>
    public bool TrayAutostart { get; set; } = true;

    private JsonObject SetTrayAutostart(JsonObject a)
    {
        if (a.Get("enable") is not JsonValue v || v.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            return Fail("invalid_argument", M("Нужен аргумент enable: true или false", "Argument enable must be true or false", "参数 enable 必须为 true 或 false"));
        TrayAutostart = a.Bool("enable");
        return Ok(new() { ["tray_autostart"] = TrayAutostart });
    }

    /// <summary>status.install_id: tests set a new one to simulate a clean reinstall (REMOVEDATA).</summary>
    public string InstallId { get; set; } = "7c1e2d9a-0f3b-4d61-9a55-fake00000001";

    public List<string> Calls { get; } = [];

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Puts the fake service into a named state (see Scenarios); unknown names mean "running".</summary>
    public void SetScenario(string scenario)
    {
        lock (_gate)
        {
            _jobCts?.Cancel();
            Scenario = Scenarios.All.Contains(scenario) ? scenario : Scenarios.Running;
            var now = Now();
            _enabledItems.Clear();
            _enabledItems.UnionWith(["zaprett-exclude", "zaprett-exclude-ipset", "user-hosts-exclude", "user-ipset-exclude"]);
            if (Scenario != Scenarios.FirstRun)
                _enabledItems.UnionWith(["zaprett-youtube", "zaprett-discord", "zaprett-rutracker"]);
            Available = Scenario != Scenarios.Unavailable;
            Enabled = Scenario is not (Scenarios.Stopped or Scenarios.FirstRun);
            Running = Enabled && Scenario != Scenarios.Error;
            _degraded = Scenario == Scenarios.Degraded;
            CanModify = Scenario != Scenarios.ReadOnly;
            _statusFrom = Scenario == Scenarios.SlowStart ? DateTimeOffset.UtcNow + SlowStartDelay : DateTimeOffset.MinValue;
            StrategyId = "strategy-general";
            _startedAt = now - 2 * 3600 - 14 * 60;
            _job = null;
            _diagnose = null;
            _testResults = null;
            _userTexts["user-hosts"] = "example.org\nkinozal.tv\n";
            _userTexts["user-hosts-exclude"] = "sberbank.ru\ngosuslugi.ru\n";
            _userTexts["user-ipset"] = "";
            _userTexts["user-ipset-exclude"] = "";
            _userStrategies.Clear();
            _userStrategies["user-my-youtube"] = "# my strategy\n--filter-tcp=443 ${hostlists} --dpi-desync=fake,multidisorder --dpi-desync-split-pos=midsld --dpi-desync-repeats=8 --dpi-desync-fooling=md5sig,badseq\n";
            _config = DefaultConfig();
            _config["main"]!["enabled"] = Enabled;
            Autostart = Enabled;
            _config["main"]!["autostart"] = Autostart;
            InitSources(now);
            _monitor = BuildMonitor(now);
            _probe = Scenario == Scenarios.FirstRun ? null : BuildProbe(now - 300);
            if (Scenario == Scenarios.Testing)
                _job = NewJob("test", ProgressMessage("test", 5, 12), 45);
            else if (Scenario != Scenarios.FirstRun)
                _job = FinishedJob("probe", now - 300);
        }
    }

    /// <summary>What the "Start service" button does in fake mode (no UAC): the service becomes reachable.</summary>
    public void SimulateServiceStart()
    {
        lock (_gate)
            Available = true;
        Publish("status", StatusEvent());
    }

    public ValueTask DisposeAsync()
    {
        _jobCts?.Cancel();
        lock (_gate)
            foreach (var c in _subscribers)
                c.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ServiceEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        if (!Available)
            throw new ZaprettUnavailableException("Служба zaprett не запущена (поддельный режим)");
        var channel = Channel.CreateUnbounded<ServiceEvent>();
        lock (_gate)
            _subscribers.Add(channel);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct))
                yield return ev;
        }
        finally
        {
            lock (_gate)
                _subscribers.Remove(channel);
        }
        if (!Available)
            throw new ZaprettUnavailableException("Служба zaprett остановлена (поддельный режим)");
    }

    private void Publish(string type, JsonObject data)
    {
        List<Channel<ServiceEvent>> subs;
        lock (_gate)
            subs = [.. _subscribers];
        foreach (var s in subs)
            s.Writer.TryWrite(new ServiceEvent(type, data.Clone()));
    }

    public async Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(40 * Math.Min(1, JobSeconds)), ct);
        lock (_gate)
        {
            Calls.Add(method);
            _lang = Loc.L.Normalize(args?.Str("lang"));
        }
        if (!Available)
            throw new ZaprettUnavailableException("Служба zaprett не запущена (поддельный режим)");
        if (method is "status" or "page" && _statusFrom - DateTimeOffset.UtcNow is { Ticks: > 0 } wait)
            await Task.Delay(wait, ct);
        if (!CanModify && Services.ServiceAccess.Modifies(method))
            return Fail("access_denied", M("Недостаточно прав", "Access denied", "权限不足"));
        lock (_gate)
            return Dispatch(method, args ?? []);
    }

    private JsonObject Dispatch(string method, JsonObject a) => method switch
    {
        "status" => BuildStatus(),
        "version" => Ok(new() { ["version"] = ProductVersion, ["winws"] = "v72.13", ["winws2"] = "1.0.5.2" }),
        "presets" => BuildPresets(),
        "page" => BuildPage(a.Str("name") ?? "overview"),
        "start" => Service(true, true),
        "stop" => Service(false, Enabled),
        "restart" => Enabled ? Service(true, true) : Fail("disabled", M("Обход выключен. Нажмите «Включить».", "The bypass is off. Press 'Turn on'.", "绕过已关闭，请点击“开启”。")),
        "tray.autostart" => SetTrayAutostart(a),
        "autostart" => SetAutostart(a),
        "enable" => SetAutostartAnd(Service(Running, true), true),
        "disable" => SetAutostartAnd(Service(false, false), false),
        "check" => BuildCheck(),
        "items" => BuildItems(a.Str("type")),
        "list.enable" or "list.disable" => ToggleItem(a.Str("id"), method == "list.enable"),
        "strategy.set" => SetStrategy(a.Str("id")),
        "strategy.show" => ShowStrategy(a.Str("id")),
        "strategy.save" => SaveStrategy(a.Str("id"), a.Str("text")),
        "strategy.delete" => DeleteStrategy(a.Str("id")),
        "user.get" => _userTexts.TryGetValue(a.Str("id") ?? "", out var t)
            ? Ok(new() { ["id"] = a.Str("id"), ["text"] = t })
            : Fail("not_found", M("Список не найден", "The list was not found", "未找到该列表")),
        "user.set" => SetUserList(a.Str("id"), a.Str("text") ?? ""),
        "mode" => SetConfig("main", "list_mode", a.Str("mode") is "whitelist" or "blacklist" ? a.Str("mode") : null),
        "engine" => SetConfig("main", "engine", a.Str("engine") is "winws" or "winws2" ? a.Str("engine") : null),
        "wizard.apply" => WizardApply(a.Strings("services")),
        "conflicts" => BuildConflicts(),
        "settings.get" => Ok(new() { ["settings"] = _config.Clone() }),
        "settings.set" => SettingsSet(a),
        "probe" => StartJob("probe", 3, FinishProbe),
        "probe.status" => Ok(new() { ["probe"] = _probe?.Clone() }),
        "monitor.status" => Ok(new() { ["monitor"] = _monitor.Clone() }),
        "diagnose" => StartJob("diagnose", 4, FinishDiagnose),
        "diagnose.status" => Ok(new() { ["diagnose"] = _diagnose?.Clone() }),
        "dns.status" => Ok(new() { ["dns"] = BuildDns() }),
        "dns.setup" => SetDns(a.Bool("enable", true)),
        "test.start" => StartTest(a),
        "test.status" => BuildTestStatus(),
        "test.stop" or "job.cancel" => CancelJob(),
        "test.apply" => SetStrategy(a.Str("id")),
        "job.status" => Ok(new() { ["job"] = _job?.Clone() }),
        "job.log" => Ok(new() { ["lines"] = new[] { M("Задача запущена", "The task started", "任务已开始"), M("Готово", "Done", "完成") }.ToJsonArray() }),
        "log" => Ok(new() { ["lines"] = FakeLog().ToJsonArray() }),
        "diag" => Ok(new() { ["text"] = FakeDiag() }),
        "sources.list" => Ok(new() { ["sources"] = new JsonArray(_sources.Select(kv => (JsonNode?)kv.Value!.DeepClone()).ToArray()) }),
        "sources.update" => StartJob("sources-update", 3, FinishSources),
        "sources.save" => SaveSource(a),
        "sources.delete" => _sources.Remove(a.Str("name") ?? "") ? Ok() : Fail("not_found", M("Подписка не найдена", "The subscription was not found", "未找到该订阅")),
        // as the service of 0.1.0 (UpdateControlStub): program updates are not available yet
        "update.check" or "update.install" => Fail("not_supported", M("Обновление программы в этой сборке недоступно", "Program updates are not available in this build", "此版本不支持程序更新")),
        _ => Fail("unknown_method", M($"Метод «{method}» не поддерживается", $"Method '{method}' is not supported", $"不支持方法“{method}”")),
    };

    // ---------- helpers ----------

    private static JsonObject Ok(JsonObject? fields = null)
    {
        var o = new JsonObject { ["ok"] = true };
        if (fields != null)
            foreach (var (k, v) in fields.ToList())
            {
                fields.Remove(k);
                o[k] = v;
            }
        return o;
    }

    private static JsonObject Fail(string code, string message, JsonObject? extra = null)
    {
        var o = new JsonObject { ["ok"] = false, ["error"] = code, ["message"] = message };
        if (extra != null)
            foreach (var (k, v) in extra.ToList())
            {
                extra.Remove(k);
                o[k] = v;
            }
        return o;
    }

    private static JsonObject LoadPresets()
    {
        var asm = typeof(FakeZaprettClient).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("presets.json", StringComparison.Ordinal));
        if (name == null)
            return new JsonObject { ["schema"] = 1, ["services"] = new JsonArray() };
        using var s = asm.GetManifestResourceStream(name)!;
        return JsonNode.Parse(s) as JsonObject ?? [];
    }

    private const string ManifestPrefix = "Zaprett.Ui.DevFakes.manifest.";

    /// <summary>Manifests of the bundled items of the product by id: the fake takes their names and descriptions
    /// (with the _en/_zh translations) from them instead of inventing its own.</summary>
    private static readonly Lazy<Dictionary<string, JsonObject>> Manifests = new(() =>
    {
        var asm = typeof(FakeZaprettClient).Assembly;
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(ManifestPrefix, StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            if (JsonNode.Parse(s) is JsonObject m && m.Str("id") is { } id)
                result[id] = m;
        }
        return result;
    });

    private static readonly string[] ManifestTexts = ["name", "name_en", "name_zh", "description", "description_en", "description_zh"];

    private static void ApplyManifestTexts(JsonObject item)
    {
        if (item.Str("source") != "bundle" || item.Str("id") is not { } id || !Manifests.Value.TryGetValue(id, out var m))
            return;
        foreach (var key in ManifestTexts)
            if (m.Str(key) is { } text)
                item[key] = text;
    }

    private static JsonObject DefaultConfig() => (JsonObject)JsonNode.Parse("""
        {
          "schema": 1,
          "main": { "enabled": false, "engine": "winws", "strategy": "strategy-general", "strategy_winws2": "",
                    "list_mode": "whitelist", "lists": [], "exclude_lists": ["zaprett-exclude","user-hosts-exclude"], "ipsets": [],
                    "exclude_ipsets": ["zaprett-exclude-ipset","user-ipset-exclude"], "ipv6": false, "debug": false,
                    "watchdog": true, "quic_block": false, "game_filter": false,
                    "game_ports_tcp": "1024-65535", "game_ports_udp": "1024-65535",
                    "network_filter": { "mode": "all", "ssids": [], "skip_corporate": false } },
          "repo": { "url": "https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json", "autoupdate": true, "autoupdate_hour": 4 },
          "test": { "timeout": 5, "concurrency": 6, "max_domains": 20, "settle": 2 },
          "monitor": { "enabled": true, "interval": 30, "threshold": 3, "auto_repair": false, "max_targets": 5, "timeout": 8 },
          "dns": { "mode": "system" },
          "ui": { "language": "ru" },
          "update": { "channel": "stable", "check": true }
        }
        """)!;

    private void InitSources(long now)
    {
        _sources.Clear();
        _sources["cloudflare_v4"] = new JsonObject
        {
            ["name"] = "cloudflare_v4", ["enabled"] = false, ["title"] = "Cloudflare IPv4", ["type"] = "ipset",
            ["url"] = "https://www.cloudflare.com/ips-v4", ["interval_hours"] = 168, ["last_update"] = 0, ["entries"] = 0,
            ["size"] = 0, ["status"] = "never", ["error"] = null, ["ram_mib"] = 0, ["item_id"] = "src-cloudflare_v4",
        };
        _sources["refilter_domains"] = new JsonObject
        {
            ["name"] = "refilter_domains", ["enabled"] = Scenario == Scenarios.Degraded, ["title"] = M("Re:filter — домены", "Re:filter — domains", "Re:filter — 域名"), ["type"] = "list",
            ["url"] = "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst", ["interval_hours"] = 72,
            ["last_update"] = now - 26 * 3600, ["entries"] = 81234, ["size"] = 1_402_311, ["status"] = "ok", ["error"] = null,
            ["ram_mib"] = 7, ["item_id"] = "src-refilter_domains",
        };
        _sources["antifilter_allyouneed"] = new JsonObject
        {
            ["name"] = "antifilter_allyouneed", ["enabled"] = false, ["title"] = M("antifilter — IP-сети", "antifilter — IP networks", "antifilter — IP 网段"), ["type"] = "ipset",
            ["url"] = "https://antifilter.download/list/allyouneed.lst", ["interval_hours"] = 72, ["last_update"] = now - 80 * 3600,
            ["entries"] = 17012, ["size"] = 301_882, ["status"] = "failed", ["error"] = "download_failed", ["ram_mib"] = 2,
            ["item_id"] = "src-antifilter_allyouneed",
        };
    }

    private JsonObject NewJob(string name, string message, int progress) => new()
    {
        ["id"] = $"{Now()}-{Random.Shared.Next(1000, 9999)}", ["name"] = name, ["state"] = "running", ["progress"] = progress,
        ["message"] = message, ["started"] = Now(), ["finished"] = 0, ["rc"] = 0, ["result"] = new JsonObject(),
    };

    private JsonObject FinishedJob(string name, long t) => new()
    {
        ["id"] = $"{t}-4242", ["name"] = name, ["state"] = "done", ["progress"] = 100, ["message"] = M("Готово", "Done", "完成"),
        ["started"] = t - 6, ["finished"] = t, ["rc"] = 0, ["result"] = new JsonObject(),
    };

    private JsonObject StartJob(string name, double seconds, Func<JsonObject, JsonObject?> finish)
    {
        if (_job?.Str("state") == "running")
            return Fail("job_busy", M("Уже выполняется другая фоновая задача. Дождитесь её окончания.", "Another background task is running. Wait until it finishes.", "另一个后台任务正在运行，请等待其完成。"));
        var job = NewJob(name, M("Запуск", "Starting", "正在启动"), 0);
        _job = job;
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        var id = job.Str("id");
        _ = Task.Run(async () =>
        {
            const int steps = 10;
            try
            {
                for (var i = 1; i <= steps; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds * JobSeconds / steps), ct);
                    JsonObject snapshot;
                    lock (_gate)
                    {
                        if (_job?.Str("id") != id)
                            return;
                        _job["progress"] = i * 100 / steps;
                        _job["message"] = ProgressMessage(name, i, steps);
                        if (i == steps)
                        {
                            var result = finish(_job);
                            _job["state"] = "done";
                            _job["finished"] = Now();
                            _job["message"] = M("Готово", "Done", "完成");
                            if (result != null)
                                _job["result"] = result;
                        }
                        snapshot = _job.Clone();
                    }
                    Publish("job", snapshot);
                }
                lock (_gate)
                {
                    if (name == "probe")
                        Publish("probe", _probe!.Clone());
                }
                Publish("status", StatusEvent());
            }
            catch (OperationCanceledException)
            {
                // cancelled: CancelJob already set the state
            }
        }, CancellationToken.None);
        return Ok(new() { ["job"] = new JsonObject { ["id"] = id, ["name"] = name } });
    }

    /// <summary>What the service sends as a "status" event: only the engine state (CommandDispatcher.RaiseStatus).</summary>
    private JsonObject StatusEvent()
    {
        lock (_gate)
            return new JsonObject { ["running"] = Running, ["pid"] = Running ? 7412 : null };
    }

    private string ProgressMessage(string name, int i, int steps)
    {
        var candidate = TestCandidates[Math.Min(TestCandidates.Length - 1, i * TestCandidates.Length / steps)];
        return name switch
        {
            "test" => M($"Проверка {candidate} ({i} из {steps})", $"Checking {candidate} ({i} of {steps})", $"正在检查 {candidate}（{i}/{steps}）"),
            "probe" => M($"Проверка адресов ({i} из {steps})", $"Checking addresses ({i} of {steps})", $"正在检查地址（{i}/{steps}）"),
            "diagnose" => M($"Проверка сайтов ({i} из {steps})", $"Checking sites ({i} of {steps})", $"正在检查网站（{i}/{steps}）"),
            "sources-update" => M("Загрузка подписок", "Downloading subscriptions", "正在下载订阅"),
            _ => M($"Шаг {i} из {steps}", $"Step {i} of {steps}", $"第 {i}/{steps} 步"),
        };
    }

    private JsonObject CancelJob()
    {
        if (_job?.Str("state") != "running")
            return Fail("no_job", M("Нет выполняющейся задачи", "No task is running", "没有正在运行的任务"));
        _jobCts?.Cancel();
        _job["state"] = "cancelled";
        _job["finished"] = Now();
        _job["message"] = M("Отменено пользователем; исходная стратегия возвращена", "Cancelled by the user; the previous strategy is restored", "已被用户取消；已恢复原来的策略");
        var copy = _job.Clone();
        _ = Task.Run(() => Publish("job", copy));
        return Ok(new() { ["state"] = "cancelled" });
    }

    // ---------- status ----------

    private JsonObject BuildStatus()
    {
        var main = _config.Obj("main")!;
        var warnings = new List<string>();
        if (!ActiveIncludes().Any())
            warnings.Add("no_active_lists");
        if (Enabled && !Running)
            warnings.Add("not_running");
        if (_job?.Str("name") == "test" && _job.Str("state") == "running")
            warnings.Add("test_running");
        if (_degraded && Running)
            warnings.Add("monitor_degraded");
        if (_enabledItems.Contains("zaprett-rutracker") && !BuildDns().Bool("encrypted"))
            warnings.Add("dns_plain");
        var blocking = new JsonArray();
        if (Scenario == Scenarios.Blocked)
        {
            warnings.Insert(0, "conflict_blocking");
            blocking.Add(new JsonObject
            {
                ["id"] = "foreign_windivert_user", ["name"] = "winws (WinDivert)", ["kind"] = "process", ["service"] = null,
                ["path"] = @"C:\Users\user\Downloads\zapret-discord-youtube\bin\winws.exe", ["pid"] = 4312,
                ["detail"] = @"Process winws (pid 4312) is running from C:\Users\user\Downloads\zapret-discord-youtube\bin\winws.exe: uses WinDivert — it intercepts the same traffic as zaprett",
            });
        }
        return Ok(new()
        {
            ["enabled"] = Enabled,
            ["autostart"] = Autostart,
            ["autostart_separate"] = true,
            ["running"] = Running,
            ["pid"] = Running ? 7412 : null,
            ["engine"] = main.Str("engine"),
            ["engine_version"] = main.Str("engine") == "winws2" ? "1.0.5.2" : "v72.13",
            ["strategy"] = new JsonObject { ["id"] = StrategyId, ["name"] = StrategyId },
            ["list_mode"] = main.Str("list_mode"),
            ["lists"] = ActiveIncludes().Where(i => !i.Contains("ipset", StringComparison.Ordinal)).ToJsonArray(),
            ["exclude_lists"] = _enabledItems.Where(i => i.EndsWith("exclude", StringComparison.Ordinal)).ToJsonArray(),
            ["ipsets"] = ActiveIncludes().Where(i => i.Contains("ipset", StringComparison.Ordinal) || i.EndsWith("voice", StringComparison.Ordinal)).ToJsonArray(),
            ["exclude_ipsets"] = _enabledItems.Where(i => i.EndsWith("ipset-exclude", StringComparison.Ordinal) || i == "zaprett-exclude-ipset").ToJsonArray(),
            ["warnings"] = warnings.ToJsonArray(),
            ["conflicts_blocking"] = blocking,
            ["install_id"] = InstallId,
            ["tray_autostart"] = TrayAutostart,
            ["can_modify"] = CanModify,
            ["version"] = ProductVersion,
            ["job"] = _job == null ? null : new JsonObject
            {
                ["id"] = _job.Str("id"), ["name"] = _job.Str("name"), ["state"] = _job.Str("state"), ["progress"] = _job.Int("progress"),
            },
            ["monitor"] = main.Get("enabled") == null ? null : new JsonObject
            {
                ["state"] = _monitor.Str("state"), ["consecutive_failures"] = _monitor.Int("consecutive_failures"),
                ["checked_at"] = _monitor.Long("checked_at"),
            },
            ["engine_stats"] = Running ? new JsonObject { ["pid"] = 7412, ["uptime_s"] = Now() - _startedAt, ["packets"] = 184_233 } : null,
            ["platform"] = new JsonObject
            {
                ["os"] = "Windows 11 Pro", ["build"] = "26100", ["arch"] = "x64", ["hvci"] = false,
                ["smart_app_control"] = "off", ["defender"] = true,
            },
            ["windivert"] = new JsonObject
            {
                ["loaded"] = Running, ["version"] = "2.2.2",
                ["foreign"] = Scenario == Scenarios.Conflicts ? new[] { "GoodbyeDPI" }.ToJsonArray() : new JsonArray(),
            },
            ["dns"] = BuildDns(),
        });
    }

    private IEnumerable<string> ActiveIncludes() =>
        _enabledItems.Where(i => !i.Contains("exclude", StringComparison.Ordinal)).OrderBy(i => i, StringComparer.Ordinal);

    private JsonObject BuildDns() => new()
    {
        ["supported"] = true,
        ["mode"] = _config.Obj("dns").Str("mode"),
        ["encrypted"] = _config.Obj("dns").Str("mode") == "doh",
        ["provider"] = _config.Obj("dns").Str("mode") == "doh" ? "windows-doh" : null,
    };

    /// <summary>main.autostart: the bypass is turned on at the next start of Windows (separate from Enabled, "on now").</summary>
    public bool Autostart { get; private set; }

    /// <summary>Like the core: only main.autostart changes, the engine is not touched.</summary>
    private JsonObject SetAutostart(JsonObject a)
    {
        var v = a.Get("enable")?.ToString();
        if (v is not ("true" or "false" or "1" or "0"))
            return Fail("bad_args", M("Нужен аргумент enable: true или false", "Argument enable must be true or false", "参数 enable 必须为 true 或 false"));
        Autostart = v is "true" or "1";
        _config["main"]!["autostart"] = Autostart;
        return Ok(new() { ["autostart"] = Autostart, ["enabled"] = Enabled });
    }

    /// <summary>The old enable/disable set both values at once.</summary>
    private JsonObject SetAutostartAnd(JsonObject reply, bool autostart)
    {
        Autostart = autostart;
        _config["main"]!["autostart"] = autostart;
        return reply;
    }

    private JsonObject Service(bool running, bool enabled)
    {
        Enabled = enabled;
        Running = running && Scenario != Scenarios.Error;
        _config["main"]!["enabled"] = enabled;
        if (Running)
            _startedAt = Now();
        var ev = StatusEvent();
        _ = Task.Run(() => Publish("status", ev));
        if (running && !Running)
            return Fail("engine_not_running", M("Движок не запустился: WinDivert не загрузился (поддельный сценарий «error»)", "The engine did not start: WinDivert was not loaded (fake scenario 'error')", "引擎未能启动：WinDivert 未加载（模拟场景“error”）"));
        return Ok(new() { ["reloaded"] = running });
    }

    private JsonObject BuildPage(string name)
    {
        // like the service: tray_autostart is added only to the answer of "status", not to the status part of "page"
        var status = BuildStatus();
        status.Remove("tray_autostart");
        var page = Ok(new()
        {
            ["status"] = status,
            ["job"] = Ok(new() { ["job"] = _job?.Clone() }),
        });
        switch (name)
        {
            case "overview":
                page["presets"] = BuildPresets();
                page["monitor"] = Ok(new() { ["monitor"] = _monitor.Clone() });
                page["probe"] = Ok(new() { ["probe"] = _probe?.Clone() });
                page["dns"] = Ok(new() { ["dns"] = BuildDns() });
                break;
            case "lists":
                page["items"] = BuildItems(null);
                page["sources"] = Dispatch("sources.list", []);
                page["presets"] = BuildPresets();
                break;
            case "strategies":
                page["items"] = BuildItems(null);
                page["test"] = BuildTestStatus();
                break;
            case "diagnostics":
                page["monitor"] = Ok(new() { ["monitor"] = _monitor.Clone() });
                page["dns"] = Ok(new() { ["dns"] = BuildDns() });
                page["diagnose"] = Ok(new() { ["diagnose"] = _diagnose?.Clone() });
                break;
        }
        return page;
    }

    private JsonObject BuildCheck()
    {
        var args = new JsonArray("--wf-l3=ipv4", "--wf-tcp=80,443,2053,2083,2087,2096,8443", "--wf-udp=443,19294-19344,50000-50100",
            "--filter-udp=443", @"--hostlist=C:\ProgramData\zaprett\run\hostlists.txt", "--dpi-desync=fake", "--dpi-desync-repeats=6", "--new",
            "--filter-tcp=443", @"--hostlist=C:\ProgramData\zaprett\run\hostlists.txt", "--dpi-desync=fake,multidisorder",
            "--dpi-desync-split-pos=midsld");
        return Ok(new()
        {
            ["args"] = args,
            ["ports"] = new JsonObject { ["tcp"] = new JsonArray(80, 443, "2053", "2083", "2087", "2096", 8443), ["udp"] = new JsonArray(443, "19294-19344", "50000-50100") },
            ["dry_run"] = new JsonObject { ["rc"] = 0, ["output"] = "github version v72.13\nwindivert initialized. capture is started.\ndry run: all ok" },
            ["warnings"] = new JsonArray("profile_unfiltered"),
            ["details"] = new JsonObject { ["unfiltered_profiles"] = new JsonArray(new JsonObject { ["profile"] = 2, ["udp"] = new JsonArray("19294-19344", "50000-50100") }) },
        });
    }
}
