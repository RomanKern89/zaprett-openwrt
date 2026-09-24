using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using K = Zaprett.Ui.Core.Text.Kind;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>
/// Main page: one big switch and the answer to "does it work now" (plan §4.1): state of the bypass, tiles of
/// services with the live check, the monitor history (48 checks) and warnings with actions.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    public const int HistoryLength = 48;

    public HomeViewModel(AppState state, INavigator? nav) : base(state, nav)
    {
        Rebuild();
    }

    [ObservableProperty] public partial string HeroKind { get; set; } = K.None;
    [ObservableProperty] public partial string HeroTitle { get; set; } = "";
    [ObservableProperty] public partial string HeroText { get; set; } = "";
    [ObservableProperty] public partial string HeroGlyph { get; set; } = "";

    /// <summary>Another program that takes the same traffic (red hero, "More" opens Diagnostics).</summary>
    [ObservableProperty] public partial bool HasBlockingConflict { get; set; }
    [ObservableProperty] public partial string ConflictWhere { get; set; } = "";
    [ObservableProperty] public partial bool IsOn { get; set; }
    [ObservableProperty] public partial string ToggleLabel { get; set; } = "";
    [ObservableProperty] public partial string StrategyText { get; set; } = "";
    [ObservableProperty] public partial string EngineText { get; set; } = "";
    [ObservableProperty] public partial string UptimeText { get; set; } = "—";
    [ObservableProperty] public partial string ModeText { get; set; } = "";
    [ObservableProperty] public partial string? PacketsText { get; set; }
    [ObservableProperty] public partial bool HasPackets { get; set; }
    [ObservableProperty] public partial string ProbeSummary { get; set; } = "";
    [ObservableProperty] public partial bool IsProbing { get; set; }
    [ObservableProperty] public partial double ProbePercent { get; set; }
    [ObservableProperty] public partial bool HasTiles { get; set; }
    [ObservableProperty] public partial string MonitorKind { get; set; } = K.None;
    [ObservableProperty] public partial string MonitorTitle { get; set; } = "";
    [ObservableProperty] public partial string MonitorText { get; set; } = "";
    [ObservableProperty] public partial string HistorySummary { get; set; } = "";
    [ObservableProperty] public partial bool MonitorEnabled { get; set; }
    [ObservableProperty] public partial bool HasWarnings { get; set; }
    [ObservableProperty] public partial bool IsTesting { get; set; }

    public ObservableCollection<ServiceTile> Tiles { get; } = [];

    public ObservableCollection<HistoryCell> History { get; } = [];

    public ObservableCollection<WarningItem> Warnings { get; } = [];

    public JobProgress Job { get; } = new();

    protected override void OnStateChanged() => Rebuild();

    /// <summary>Recomputes every display property from the shared state.</summary>
    public void Rebuild()
    {
        var st = State.Status;
        var enabled = st.Bool("enabled");
        var running = st.Bool("running");
        var monitorState = State.Monitor.Str("state") ?? st.Obj("monitor").Str("state");
        var job = State.Job;
        var testing = job.Str("name") == "test" && job.Str("state") == "running";
        IsTesting = testing;

        var conflict = BlockingConflict.FromStatus(st);
        HasBlockingConflict = conflict != null;
        ConflictWhere = conflict?.Where ?? "";
        (HeroKind, HeroTitle, HeroText, HeroGlyph) = Hero(st, enabled, running, monitorState, testing, UiText.IsWaitingNetwork(st), conflict);
        IsOn = running;
        ToggleLabel = running ? L.T("Home.TurnOff") : L.T("Home.TurnOn");

        var strategy = st.Obj("strategy");
        StrategyText = strategy == null ? L.T("Home.NoStrategy") : L.Pick(strategy, "name") ?? strategy.Str("id") ?? "";
        EngineText = UiText.EngineName(st.Str("engine"));
        var uptime = st.Obj("engine_stats").Long("uptime_s", -1);
        UptimeText = running && uptime >= 0 ? UiText.Duration(uptime) : "—";
        var packets = st.Obj("engine_stats")?.Get("packets") != null ? st.Obj("engine_stats").Long("packets") : -1;
        HasPackets = running && packets >= 0;
        PacketsText = HasPackets ? UiText.Number(packets) : null;
        ModeText = st.Str("list_mode") == "blacklist" ? L.T("Home.Mode.Blacklist")
            : L.F("Home.Mode.Whitelist", st.Strings("lists").Count + st.Strings("ipsets").Count);

        BuildTiles();
        BuildMonitor();
        BuildWarnings(st);
        if (!IsProbing)
            Job.Update(job?.Str("state") == "running" && job.Str("name") != "probe" ? job : null);
    }

    public static (string Kind, string Title, string Text, string Glyph) Hero(JsonObject? st, bool enabled, bool running, string? monitorState, bool testing,
        bool waitingNetwork = false, BlockingConflict? conflict = null)
    {
        if (st == null)
            return (K.None, L.T("Home.Hero.Loading"), "", "");
        if (conflict != null)
            return (K.Fail, L.F("Home.Hero.Conflict", conflict.Name), L.F("Home.Hero.ConflictText", conflict.Name, conflict.Advice), "");
        if (testing)
            return (K.Info, L.T("Home.Hero.Testing"), L.T("Home.Hero.TestingText"), "");
        if (running && waitingNetwork)
            return (K.Warn, L.T("Home.Hero.Waiting"), L.T("Home.Hero.WaitingText"), "");
        if (running && monitorState == "repairing")
            return (K.Info, L.T("Home.Hero.Repairing"), L.T("Home.Hero.RepairingText"), "");
        if (running && monitorState == "degraded")
            return (K.Warn, L.T("Home.Hero.Degraded"), L.T("Home.Hero.DegradedText"), "");
        if (running)
            return (K.Ok, L.T("Home.Hero.On"), L.T("Home.Hero.OnText"), "");
        if (enabled)
            return (K.Fail, L.T("Home.Hero.Broken"), L.T("Home.Hero.BrokenText"), "");
        return (K.None, L.T("Home.Hero.Off"), L.T("Home.Hero.OffText"), "");
    }

    private void BuildTiles()
    {
        var probe = State.Probe;
        var presets = State.Presets;
        var tiles = new List<ServiceTile>();
        if (probe != null)
        {
            tiles.AddRange(probe.Objs("services").Select(s => ServiceTile.FromProbe(s, presets)));
            var when = UiText.Ago(probe.Long("finished", probe.Long("started")), DateTimeOffset.Now);
            ProbeSummary = probe.Bool("engine_running", true)
                ? L.F("Home.Probe.Summary", when)
                : L.F("Home.Probe.SummaryNoBypass", when);
        }
        else
        {
            foreach (var s in presets.Objs("services").Where(s => s.Bool("enabled") || s.Str("enabled_variant") != null))
                tiles.Add(ServiceTile.NotChecked(s.Str("id") ?? "", L.Pick(s, "name") ?? ""));
            ProbeSummary = L.T("Home.Probe.Never");
        }
        Replace(Tiles, tiles);
        HasTiles = tiles.Count > 0;
    }

    private void BuildMonitor()
    {
        var m = State.Monitor;
        MonitorEnabled = m.Bool("enabled");
        var state = m.Str("state") ?? "unknown";
        (MonitorKind, MonitorTitle, MonitorText) = state switch
        {
            "ok" => (K.Ok, L.T("Monitor.Ok"), L.T("Monitor.OkText")),
            "degraded" => (K.Fail, L.T("Monitor.Degraded"),
                UiText.RepairBlockedByConflict(m) ? L.T("Monitor.RepairBlockedConflict") : L.T("Monitor.DegradedText")),
            "repairing" => (K.Info, L.T("Monitor.Repairing"), L.T("Monitor.RepairingText")),
            _ => (K.None, L.T("Monitor.Unknown"), L.T("Monitor.UnknownText")),
        };
        if (!MonitorEnabled)
            (MonitorKind, MonitorTitle, MonitorText) = (K.None, L.T("Monitor.Off"), L.T("Monitor.OffText"));

        var cells = BuildHistory(m.Objs("history"));
        Replace(History, cells);
        var counted = cells.Count(c => c.Kind != K.None);
        var failed = cells.Count(c => c.Kind == K.Fail);
        HistorySummary = m == null ? "" : L.F("Monitor.HistorySummary", counted, failed, m.Int("interval"));
    }

    /// <summary>Last 48 checks, oldest first; a check fails when less than half of its targets opened.</summary>
    public static List<HistoryCell> BuildHistory(IEnumerable<JsonObject> history) =>
        history.TakeLast(HistoryLength)
            .Select(h => new HistoryCell(UiText.CheckKind(h.Long("ok"), h.Long("total")),
                L.F("Monitor.Cell", UiText.Time(h.Long("t")), h.Long("ok"), h.Long("total"))))
            .ToList();

    private void BuildWarnings(JsonObject? st)
    {
        // the hero already names the interfering program and leads to the details: no second, yellow copy of it
        var items = st.Strings("warnings")
            .Where(code => code != "conflict_blocking")
            .Select(code => UiText.Warning(code, st))
            .OrderBy(w => w.IsInfo)
            .Select(w => new WarningItem(w, DoWarningAction))
            .ToList();
        Replace(Warnings, items);
        HasWarnings = items.Count > 0;
    }

    private async Task DoWarningAction(string action)
    {
        if (action == "restart")
            await Try(() => State.CallAsync("restart"), "Home.Err.Restart");
        else
            Nav?.Navigate(action);
        await RefreshQuietAsync();
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var i in items)
            target.Add(i);
    }

    private async Task RefreshQuietAsync()
    {
        try
        {
            await State.RefreshAsync();
        }
        catch (ZaprettCallException)
        {
            // the shell shows an unavailable service
        }
    }

    /// <summary>"More" of a blocking conflict: the conflicts are listed on the Diagnostics page.</summary>
    [RelayCommand]
    private void OpenConflicts() => Open("diagnostics");

    /// <summary>The big switch: start when off, stop when on (autostart stays as it is, see Settings).</summary>
    [RelayCommand]
    private async Task Toggle()
    {
        var turnOn = !State.Status.Bool("running");
        var ok = await Try(() => State.CallAsync(turnOn ? "start" : "stop"), turnOn ? "Home.Err.Start" : "Home.Err.Stop");
        if (ok)
            HasMessage = false;
        await RefreshQuietAsync();
    }

    /// <summary>"Check now": live check of the enabled services without restarting the engine (probe).</summary>
    [RelayCommand]
    private async Task CheckNow()
    {
        if (IsProbing)
            return;
        IsProbing = true;
        ProbePercent = 0;
        try
        {
            await Try(async () =>
            {
                var (_, job) = await State.RunJobAsync("probe", null, j => ProbePercent = j.Long("progress"));
                if (job != null && job.Str("state") != "done")
                    ShowMessage(MessageKind.Warning, L.T("Home.Err.Probe"), job.Str("message") ?? UiText.JobState(job.Str("state")));
                var reply = await State.CallAsync("probe.status");
                State.Probe = reply.Obj("probe");
            }, "Home.Err.Probe", busy: false);
        }
        finally
        {
            IsProbing = false;
        }
        await RefreshQuietAsync();
    }
}
