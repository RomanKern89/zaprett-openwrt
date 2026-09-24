using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>A choice of a combo box: service value and its label.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Settings": service settings (config.json, ARCHITECTURE-WIN §5) saved together with settings.set, plus
/// autostart/engine/DNS that have their own methods, and preferences of the interface itself (language, theme).
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public static readonly int[] Intervals = [10, 15, 20, 30, 60];

    private JsonObject? _loaded;
    private bool _loading;

    public SettingsViewModel(AppState state, INavigator? nav) : base(state, nav)
    {
        Engines = [new("winws", L.T("Settings.Engine.Winws")), new("winws2", L.T("Settings.Engine.Winws2"))];
        DnsModes = [new("system", L.T("Settings.Dns.System")), new("doh", L.T("Settings.Dns.Doh"))];
        NetworkModes = [new("all", L.T("Settings.Net.All")), new("ssids", L.T("Settings.Net.Ssids"))];
        IntervalChoices = Intervals.Select(i => new Choice(i.ToString(System.Globalization.CultureInfo.InvariantCulture), L.F("Fmt.EveryMin", i))).ToList();
        Channels = [new("stable", L.T("Settings.Update.Stable")), new("beta", L.T("Settings.Update.Beta"))];
        Languages = L.Languages.Select(l => new Choice(l.Code, l.Name)).ToList();
        Themes = [new("system", L.T("Settings.Theme.System")), new("light", L.T("Settings.Theme.Light")), new("dark", L.T("Settings.Theme.Dark"))];
        _loading = true;
        Language = Languages.FirstOrDefault(l => l.Value == L.Language) ?? Languages[0];
        Theme = Themes.First(t => t.Value == state.Prefs.Theme);
        _loading = false;
    }

    public IReadOnlyList<Choice> Engines { get; }
    public IReadOnlyList<Choice> DnsModes { get; }
    public IReadOnlyList<Choice> NetworkModes { get; }
    public IReadOnlyList<Choice> IntervalChoices { get; }
    public IReadOnlyList<Choice> Channels { get; }
    public IReadOnlyList<Choice> Languages { get; }
    public IReadOnlyList<Choice> Themes { get; }

    [ObservableProperty] public partial bool IsLoaded { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial bool Autostart { get; set; }
    [ObservableProperty] public partial Choice? Engine { get; set; }
    [ObservableProperty] public partial Choice? DnsMode { get; set; }
    [ObservableProperty] public partial string DnsState { get; set; } = "";
    [ObservableProperty] public partial bool DnsSupported { get; set; } = true;
    [ObservableProperty] public partial bool QuicBlock { get; set; }
    [ObservableProperty] public partial bool GameFilter { get; set; }
    [ObservableProperty] public partial string GamePortsTcp { get; set; } = "";
    [ObservableProperty] public partial string GamePortsUdp { get; set; } = "";
    [ObservableProperty] public partial bool Ipv6 { get; set; }
    [ObservableProperty] public partial bool Watchdog { get; set; }
    [ObservableProperty] public partial Choice? NetworkMode { get; set; }
    [ObservableProperty] public partial string Ssids { get; set; } = "";
    [ObservableProperty] public partial bool SkipCorporate { get; set; }
    [ObservableProperty] public partial bool MonitorEnabled { get; set; }
    [ObservableProperty] public partial Choice? MonitorInterval { get; set; }
    [ObservableProperty] public partial double MonitorThreshold { get; set; } = 3;
    [ObservableProperty] public partial bool AutoRepair { get; set; }
    [ObservableProperty] public partial Choice? Channel { get; set; }
    [ObservableProperty] public partial bool CheckUpdates { get; set; }
    [ObservableProperty] public partial string UpdateText { get; set; } = "";

    /// <summary>
    /// The icon at Windows sign-in: HKLM Run value "zaprett" = "…\zaprett-ui.exe" --tray (written by the installer,
    /// TRAYAUTOSTART). Only the service may change HKLM, so the value is read from status.tray_autostart (the registry
    /// as the service sees it now) and changed with tray.autostart {enable}. A service without the field: no switch.
    /// </summary>
    [ObservableProperty] public partial bool TrayAutostart { get; set; }

    /// <summary>A tray.autostart call is on its way: a refresh in between must not move the switch back.</summary>
    private bool _trayChanging;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayAutostartHint), nameof(CanChangeTrayAutostart))]
    public partial bool TrayAutostartDenied { get; set; }

    /// <summary>The field is there but null: the service could not read the registry, the state is unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayAutostartHint), nameof(CanChangeTrayAutostart))]
    public partial bool TrayAutostartUnknown { get; set; }

    /// <summary>
    /// The service reported the field at least once. Only the answer of "status" carries it (the service adds it in
    /// front of the core; the "page" overview the shared state is refreshed with does not, checked on Windows 10 on
    /// 2026-09-24), so absence in a later refresh does not hide the switch. An older service never reports it: no switch.
    /// </summary>
    [ObservableProperty] public partial bool TrayAutostartSupported { get; set; }

    public bool CanChangeTrayAutostart => CanModify && !TrayAutostartDenied && !TrayAutostartUnknown;

    public string TrayAutostartHint => TrayAutostartDenied || !CanModify ? L.T("Settings.TrayAutostartDenied")
        : TrayAutostartUnknown ? L.T("Settings.TrayAutostartUnknown")
        : L.T("Settings.TrayAutostartText");

    /// <summary>Page of the releases: the manual way to update while the service cannot update the program.</summary>
    public const string ReleasesUrl = "https://github.com/RomanKern89/zaprett-openwrt/releases";

    public Uri ReleasesUri { get; } = new(ReleasesUrl);

    /// <summary>null until the service has answered; the update controls appear only when it can update, the manual
    /// instructions only when it answered not_supported (the controls come back by themselves once updates exist).</summary>
    [ObservableProperty] public partial bool? UpdatesSupported { get; set; }

    public bool ShowUpdateControls => UpdatesSupported == true;

    public bool ShowUpdatesUnavailable => UpdatesSupported == false;

    partial void OnUpdatesSupportedChanged(bool? value)
    {
        State.UpdatesSupported = value;
        OnPropertyChanged(nameof(ShowUpdateControls));
        OnPropertyChanged(nameof(ShowUpdatesUnavailable));
    }
    [ObservableProperty] public partial bool UpdateAvailable { get; set; }
    [ObservableProperty] public partial Choice? Language { get; set; }
    [ObservableProperty] public partial Choice? Theme { get; set; }
    [ObservableProperty] public partial bool Notifications { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial string VersionText { get; set; } = "";

    public bool IsSsidMode => NetworkMode?.Value == "ssids";

    public override async Task LoadAsync()
    {
        await Try(async () =>
        {
            var r = await State.CallAsync("settings.get");
            Load(r.Obj("settings") ?? r.Obj("config") ?? []);
            try
            {
                var v = await State.CallAsync("version");
                VersionText = L.F("Settings.Version", v.Str("version") ?? "?", v.Str("winws") ?? v.Str("nfqws") ?? "?", v.Str("winws2") ?? v.Str("nfqws2") ?? "—");
            }
            catch (ZaprettCallException)
            {
                VersionText = "";
            }
        }, "Settings.Err.Load");
        await ProbeUpdatesAsync();
        // switches get their values after the page is shown: a ToggleSwitch whose template is applied while it is
        // already on was drawn with pale colours in the screenshots of 2026-09-23
        _loading = true;
        Notifications = State.Prefs.Notifications;
        CloseToTray = State.Prefs.CloseToTray;
        _loading = false;
        await LoadTrayAutostartAsync();
    }

    /// <summary>Asks "status" itself: the shared state may not have it yet (the page opened before the first answer)
    /// or may have it from "page" without tray_autostart.</summary>
    private async Task LoadTrayAutostartAsync()
    {
        await ApplyTrayAutostart(State.Status);
        try
        {
            await ApplyTrayAutostart(await State.CallAsync("status"));
        }
        catch (ZaprettCallException)
        {
            // the state keeps what it had; the next refresh or opening of the page tries again
        }
    }

    /// <summary>Takes the switch from an answer that has the field; an answer without it changes nothing. When the
    /// card appears only now, the switch is turned on after it is shown: a ToggleSwitch whose template is applied while
    /// it is already on is drawn pale (seen on 2026-09-24 with the fake scenario slowstart).</summary>
    private async Task ApplyTrayAutostart(JsonObject? status)
    {
        if (status?.ContainsKey("tray_autostart") != true || _trayChanging)
            return;
        var unknown = status["tray_autostart"] is not JsonValue;
        var on = status.Bool("tray_autostart");
        if (!TrayAutostartSupported)
        {
            SetTrayAutostartSilently(false, unknown);
            TrayAutostartSupported = true;
            await Task.Delay(100);
            if (_trayChanging)
                return;
        }
        SetTrayAutostartSilently(on, unknown);
    }

    private void SetTrayAutostartSilently(bool on, bool unknown)
    {
        var loading = _loading;
        _loading = true;
        TrayAutostartUnknown = unknown;
        TrayAutostart = on;
        _loading = loading;
    }

    protected override void OnStateChanged() => _ = ApplyTrayAutostart(State.Status);

    public void Load(JsonObject config)
    {
        _loading = true;
        _loaded = config.Clone();
        var main = config.Obj("main");
        Autostart = State.Status != null ? AppState.AutostartOf(State.Status)
            : main.Get("autostart") != null ? main.Bool("autostart") : main.Bool("enabled");
        Engine = Engines.FirstOrDefault(e => e.Value == main.Str("engine")) ?? Engines[0];
        DnsMode = DnsModes.FirstOrDefault(d => d.Value == config.Obj("dns").Str("mode")) ?? DnsModes[0];
        var dns = State.Dns ?? State.Status.Obj("dns");
        DnsSupported = dns.Bool("supported", true);
        DnsState = !DnsSupported ? L.T("Settings.Dns.NotSupported")
            : dns.Bool("encrypted") ? L.T("Settings.Dns.StateOn") : L.T("Settings.Dns.StateOff");
        QuicBlock = main.Bool("quic_block");
        GameFilter = main.Bool("game_filter");
        GamePortsTcp = main.Str("game_ports_tcp") ?? "1024-65535";
        GamePortsUdp = main.Str("game_ports_udp") ?? "1024-65535";
        Ipv6 = main.Bool("ipv6");
        Watchdog = main.Bool("watchdog", true);
        var net = main.Obj("network_filter");
        NetworkMode = NetworkModes.FirstOrDefault(n => n.Value == net.Str("mode")) ?? NetworkModes[0];
        Ssids = string.Join("\n", net.Strings("ssids"));
        SkipCorporate = net.Bool("skip_corporate");
        var mon = config.Obj("monitor");
        MonitorEnabled = mon.Bool("enabled", true);
        MonitorInterval = IntervalChoices.FirstOrDefault(i => i.Value == mon.Int("interval", 30).ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? IntervalChoices[3];
        MonitorThreshold = Math.Clamp(mon.Int("threshold", 3), 1, 20);
        AutoRepair = mon.Bool("auto_repair");
        var upd = config.Obj("update");
        Channel = Channels.FirstOrDefault(c => c.Value == upd.Str("channel")) ?? Channels[0];
        CheckUpdates = upd.Bool("check", true);
        IsLoaded = true;
        IsDirty = false;
        _loading = false;
    }

    /// <summary>Only the changed keys of config.json, in its shape (settings.set does a partial update).</summary>
    public JsonObject BuildPatch()
    {
        var main = new JsonObject
        {
            ["quic_block"] = QuicBlock, ["game_filter"] = GameFilter, ["game_ports_tcp"] = GamePortsTcp.Trim(),
            ["game_ports_udp"] = GamePortsUdp.Trim(), ["ipv6"] = Ipv6, ["watchdog"] = Watchdog,
            ["network_filter"] = new JsonObject
            {
                ["mode"] = NetworkMode?.Value ?? "all",
                ["ssids"] = Ssids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToJsonArray(),
                ["skip_corporate"] = SkipCorporate,
            },
        };
        var monitor = new JsonObject
        {
            ["enabled"] = MonitorEnabled,
            ["interval"] = int.Parse(MonitorInterval?.Value ?? "30", System.Globalization.CultureInfo.InvariantCulture),
            // an emptied NumberBox gives NaN: keep the loaded value then
            ["threshold"] = double.IsNaN(MonitorThreshold) ? _loaded.Obj("monitor").Int("threshold", 3) : (int)Math.Clamp(Math.Round(MonitorThreshold), 1, 20),
            ["auto_repair"] = AutoRepair,
        };
        var update = new JsonObject { ["channel"] = Channel?.Value ?? "stable", ["check"] = CheckUpdates };
        var full = new JsonObject { ["main"] = main, ["monitor"] = monitor, ["update"] = update };
        return Diff(full, _loaded ?? []);
    }

    /// <summary>Keeps only values that differ from the loaded config (nested objects compared per key).</summary>
    public static JsonObject Diff(JsonObject wanted, JsonObject loaded)
    {
        var patch = new JsonObject();
        foreach (var (key, value) in wanted)
        {
            var old = loaded.Get(key);
            if (value is JsonObject wo && old is JsonObject oo && key != "network_filter")
            {
                var sub = Diff(wo, oo);
                if (sub.Count > 0)
                    patch[key] = sub;
            }
            else if (!JsonNode.DeepEquals(value, old))
            {
                patch[key] = value?.DeepClone();
            }
        }
        return patch;
    }

    /// <summary>Checks the port ranges of the game filter: "1024-65535" or "80,443,1000-2000".</summary>
    public static bool IsPortList(string text) =>
        text.Split(',').All(part =>
        {
            var p = part.Trim().Split('-');
            return p.Length is 1 or 2 && p.All(x => int.TryParse(x, out var n) && n is >= 1 and <= 65535)
                && (p.Length == 1 || int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture) <= int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
        });

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(CanModify))
        {
            OnPropertyChanged(nameof(CanChangeTrayAutostart));
            OnPropertyChanged(nameof(TrayAutostartHint));
        }
        if (_loading)
            return;
        switch (e.PropertyName)
        {
            case nameof(QuicBlock) or nameof(GameFilter) or nameof(GamePortsTcp) or nameof(GamePortsUdp) or nameof(Ipv6) or nameof(Watchdog)
                or nameof(NetworkMode) or nameof(Ssids) or nameof(SkipCorporate) or nameof(MonitorEnabled) or nameof(MonitorInterval)
                or nameof(MonitorThreshold) or nameof(AutoRepair) or nameof(Channel) or nameof(CheckUpdates):
                IsDirty = BuildPatch().Count > 0;
                if (e.PropertyName == nameof(NetworkMode))
                    OnPropertyChanged(nameof(IsSsidMode));
                break;
            case nameof(TrayAutostart):
                _ = ChangeTrayAutostartAsync(TrayAutostart);
                break;
            case nameof(Autostart):
                var autostart = Autostart;
                // only the behaviour after a restart: the bypass stays as it is now (the button on the home page)
                _ = CallOrRevert(() => State.SetAutostartAsync(autostart), "Settings.Err.Autostart", () => Autostart = !autostart);
                break;
            case nameof(Engine) when Engine != null:
                var engine = Engine.Value;
                var previous = _loaded.Obj("main").Str("engine") ?? "winws";
                _ = CallOrRevert(() => State.CallAsync("engine", new JsonObject { ["engine"] = engine }), "Settings.Err.Engine",
                    () => Engine = Engines.FirstOrDefault(e => e.Value == previous) ?? Engines[0]);
                break;
            case nameof(DnsMode) when DnsMode != null:
                _ = ChangeDnsAsync(DnsMode.Value);
                break;
            case nameof(Language) when Language != null:
                _ = ChangeLanguageAsync(Language.Value);
                break;
            case nameof(Theme) when Theme != null:
                State.Prefs.Theme = Theme.Value;
                State.Prefs.Save();
                State.Platform.ApplyTheme(Theme.Value);
                break;
            case nameof(Notifications):
                State.Prefs.Notifications = Notifications;
                State.Prefs.Save();
                break;
            case nameof(CloseToTray):
                State.Prefs.CloseToTray = CloseToTray;
                State.Prefs.Save();
                break;
        }
    }

    /// <summary>A switch that calls the service at once goes back to its old position when the call fails
    /// (for example "access_denied" for a user without rights), so the page never shows a state that is not there.</summary>
    /// <summary>The service writes HKLM and answers with the value read back; a refusal for lack of rights leaves the
    /// switch disabled with the explanation (the user is not an administrator nor in "zaprett Operators").</summary>
    private async Task ChangeTrayAutostartAsync(bool enable)
    {
        _trayChanging = true;
        try
        {
            var r = await State.CallAsync("tray.autostart", new JsonObject { ["enable"] = enable });
            _loading = true;
            TrayAutostart = r["tray_autostart"] is JsonValue ? r.Bool("tray_autostart") : enable;
            _loading = false;
            State.RequestRefresh();
        }
        catch (ZaprettCallException e)
        {
            _loading = true;
            TrayAutostart = !enable;
            _loading = false;
            if (e.Code == "access_denied")
                TrayAutostartDenied = true;
            ShowError(L.T("Settings.Err.TrayAutostart"), e);
        }
        finally
        {
            _trayChanging = false;
        }
    }

    private async Task CallOrRevert(Func<Task> call, string failTitleKey, Action revert)
    {
        if (await Try(call, failTitleKey, busy: false))
            return;
        _loading = true;
        revert();
        _loading = false;
    }

    /// <summary>The language is stored in config.json (ui.language) so the service and the CLI speak it too; the
    /// interface switches at once even if the service cannot save it (the local copy keeps it for the next start).</summary>
    private async Task ChangeLanguageAsync(string lang)
    {
        State.Prefs.Language = lang;
        // without the rights the language is this user's own: nothing is sent, and the shared one does not replace it
        State.Prefs.LanguagePersonal = !CanModify;
        State.Prefs.Save();
        if (!CanModify)
        {
            State.Platform.ApplyLanguage(lang);
            return;
        }
        await Try(() => State.CallAsync("settings.set", new JsonObject { ["ui"] = new JsonObject { ["language"] = lang } }),
            "Settings.Err.Save", busy: false);
        State.Platform.ApplyLanguage(lang);
    }

    private async Task ChangeDnsAsync(string mode)
    {
        var ok = await Try(async () =>
        {
            // ARCHITECTURE-WIN §13.1: dns.setup {enable}
            await State.RunJobAsync("dns.setup", new JsonObject { ["enable"] = mode == "doh" }, null);
            State.Dns = (await State.CallAsync("dns.status")).Obj("dns");
            DnsState = State.Dns.Bool("encrypted") ? L.T("Settings.Dns.StateOn") : L.T("Settings.Dns.StateOff");
        }, "Dns.Err", busy: false);
        if (!ok)
        {
            _loading = true;
            DnsMode = DnsModes.FirstOrDefault(d => d.Value == (mode == "doh" ? "system" : "doh")) ?? DnsModes[0];
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!IsPortList(GamePortsTcp) || !IsPortList(GamePortsUdp))
        {
            ShowMessage(MessageKind.Warning, L.T("Settings.BadPorts"), L.T("Settings.BadPortsText"));
            return;
        }
        var patch = BuildPatch();
        if (patch.Count == 0)
        {
            IsDirty = false;
            return;
        }
        await Try(async () =>
        {
            var r = await State.CallAsync("settings.set", patch);
            Load(r.Obj("settings") ?? (await State.CallAsync("settings.get")).Obj("settings") ?? []);
            ShowMessage(MessageKind.Success, L.T("Settings.Saved"), r.Bool("reloaded") ? L.T("Settings.SavedReloaded") : L.T("Settings.SavedText"));
        }, "Settings.Err.Save");
    }

    [RelayCommand]
    private void Revert()
    {
        if (_loaded != null)
            Load(_loaded);
    }

    /// <summary>Asks the service once per session whether it can update the program (the first update.check).</summary>
    private async Task ProbeUpdatesAsync()
    {
        if (State.UpdatesSupported is { } known)
        {
            UpdatesSupported = known;
            return;
        }
        try
        {
            ShowUpdateCheck(await State.CallAsync("update.check"));
            UpdatesSupported = true;
        }
        catch (ZaprettCallException e) when (e.Code == "not_supported")
        {
            UpdatesSupported = false;
        }
        catch (ZaprettCallException e) when (e.Code != "service_unavailable")
        {
            // updates exist, the check itself failed (no network, server): the controls stay to try again
            UpdatesSupported = true;
            UpdateText = e.Text;
        }
        catch (ZaprettCallException)
        {
            // service down: nothing known yet, the next visit asks again
        }
    }

    private void ShowUpdateCheck(JsonObject r)
    {
        UpdateAvailable = r.Bool("update_available");
        UpdateText = UpdateAvailable ? L.F("Settings.Update.Available", r.Str("latest") ?? "?") : L.F("Settings.Update.Latest", r.Str("current") ?? r.Str("latest") ?? "?");
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        await Try(async () =>
        {
            try
            {
                ShowUpdateCheck(await State.CallAsync("update.check"));
            }
            catch (ZaprettCallException e) when (e.Code == "not_supported")
            {
                UpdatesSupported = false;
            }
        }, "Settings.Err.Update");
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (!await State.Platform.ConfirmAsync(L.T("Settings.Update.Install"), L.T("Settings.Update.InstallText"), L.T("Settings.Update.InstallButton")))
            return;
        await Try(async () =>
        {
            var (_, job) = await State.RunJobAsync("update.install", null, null);
            if (job != null && job.Str("state") != "done")
                throw new ZaprettCallException("update_failed", job.Str("message"));
            UpdateText = L.T("Settings.Update.Installing");
        }, "Settings.Err.Update");
    }

    [RelayCommand]
    private void RunWizard()
    {
        State.Prefs.WizardDone = false;
        State.Prefs.WizardDoneFor = null;
        State.Prefs.Save();
        Nav?.Navigate("wizard");
    }
}
