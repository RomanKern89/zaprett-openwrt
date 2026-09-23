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

/// <summary>An installed strategy of the current engine.</summary>
public sealed record StrategyItem(string Id, string Name, string Description, string SourceLabel, bool IsActive, bool IsUser)
{
    public string ActiveLabel => IsActive ? L.T("Strategies.Current") : "";
}

/// <summary>A row of the automatic selection results (router contract §10: sorted by ratio ↓, avg_ms ↑).</summary>
public sealed record TestRow(int Rank, string Id, string Name, string Score, string Percent, double Ratio, string Time, string StatusLabel, string Kind, bool CanApply, bool IsCurrent)
{
    public double Bar => Ratio * 100;

    public bool HasStatus => StatusLabel.Length > 0;

    public static TestRow From(int rank, JsonObject r, string? current)
    {
        var status = r.Str("status") ?? "done";
        var ok = r.Long("ok");
        var total = r.Long("total");
        var ratio = r.Double("ratio", total > 0 ? (double)ok / total : 0);
        var kind = status != "done" ? K.None : ratio >= 1 ? K.Ok : ratio > 0 ? K.Warn : K.Fail;
        var label = status switch
        {
            "done" => "",
            "invalid" => L.T("Test.Status.Invalid"),
            "start_failed" => L.T("Test.Status.StartFailed"),
            "skipped" => L.T("Test.Status.Skipped"),
            _ => status,
        };
        var id = r.Str("id") ?? "";
        return new TestRow(rank, id, L.Pick(r, "name") ?? id, status == "done" ? $"{ok}/{total}" : "—", status == "done" ? UiText.Percent(ratio) : "",
            ratio, UiText.Ms(r.Long("avg_ms")), label, kind, status == "done" && ok > 0 && id != current, id == current);
    }
}

/// <summary>
/// "Strategies": the current one and the installed ones; automatic selection (quick/full, isolated/exclusive,
/// progress, results, apply the best); viewing and editing of own strategies with highlighting and a check.
/// </summary>
public sealed partial class StrategiesViewModel(AppState state, INavigator? nav) : PageViewModel(state, nav)
{
    public const int MaxStrategyBytes = 64 * 1024;

    public ObservableCollection<StrategyItem> Strategies { get; } = [];
    public ObservableCollection<TestRow> Results { get; } = [];
    public ObservableCollection<WarningItem> CheckWarnings { get; } = [];
    public JobProgress Job { get; } = new();

    [ObservableProperty] public partial string CurrentName { get; set; } = "";
    [ObservableProperty] public partial string EngineText { get; set; } = "";
    [ObservableProperty] public partial bool QuickMode { get; set; } = true;
    [ObservableProperty] public partial bool ForceExclusive { get; set; }
    [ObservableProperty] public partial bool ApplyIfBetter { get; set; } = true;
    [ObservableProperty] public partial bool IsTesting { get; set; }
    [ObservableProperty] public partial string ModeText { get; set; } = "";
    [ObservableProperty] public partial string BaselineText { get; set; } = "";
    [ObservableProperty] public partial string ResultText { get; set; } = "";
    [ObservableProperty] public partial bool HasResults { get; set; }
    [ObservableProperty] public partial StrategyItem? Selected { get; set; }
    [ObservableProperty] public partial string EditorText { get; set; } = "";
    [ObservableProperty] public partial string EditorName { get; set; } = "";
    [ObservableProperty] public partial bool IsEditable { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial string SelectedInfo { get; set; } = "";
    [ObservableProperty] public partial string CheckText { get; set; } = "";
    [ObservableProperty] public partial string CheckKind { get; set; } = K.None;
    [ObservableProperty] public partial bool HasCheck { get; set; }
    [ObservableProperty] public partial string EngineOutput { get; set; } = "";
    [ObservableProperty] public partial string ArgsText { get; set; } = "";
    [ObservableProperty] public partial string PortsText { get; set; } = "";

    /// <summary>Full selection (all installed strategies) — the pair of QuickMode for radio buttons.</summary>
    public bool FullMode
    {
        get => !QuickMode;
        set => QuickMode = !value;
    }

    partial void OnQuickModeChanged(bool value) => OnPropertyChanged(nameof(FullMode));

    public bool IsNotTesting => !IsTesting;

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(IsNotTesting));

    public bool IsWinws2 => State.Status.Str("engine") is "winws2" or "nfqws2";

    public override async Task LoadAsync()
    {
        await Try(async () =>
        {
            var page = await State.CallAsync("page", new JsonObject { ["name"] = "strategies" });
            FillStrategies(page.OkPart("items") ?? await State.CallAsync("items"));
            ShowTest(page.OkPart("test") ?? await State.CallAsync("test.status", new JsonObject { ["brief"] = true }));
        }, "Strategies.Err.Load");
    }

    protected override void OnStateChanged()
    {
        var st = State.Status;
        CurrentName = st.Obj("strategy") is { } s ? L.Pick(s, "name") ?? s.Str("id") ?? "" : L.T("Home.NoStrategy");
        EngineText = UiText.EngineName(st.Str("engine"));
        var job = State.Job;
        var testing = job.Str("name") == "test" && job.Str("state") == "running";
        if (testing)
            Job.Update(job);
        IsTesting = testing;
    }

    public void FillStrategies(JsonObject items)
    {
        OnStateChanged();
        var types = IsWinws2 ? new[] { "nfqws2", "winws2" } : ["nfqws", "winws"];
        var list = items.Objs("items").Where(i => types.Contains(i.Str("type")))
            .Select(i => new StrategyItem(i.Str("id") ?? "", L.Pick(i, "name") ?? i.Str("id") ?? "", L.Pick(i, "description") ?? "",
                UiText.SourceLabel(i.Str("source")), i.Bool("active"), i.Str("source") == "user"))
            .OrderByDescending(s => s.IsActive).ThenByDescending(s => s.IsUser).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keep = Selected?.Id;
        Strategies.Clear();
        foreach (var s in list)
            Strategies.Add(s);
        Selected = Strategies.FirstOrDefault(s => s.Id == keep);
    }

    /// <summary>Shows "test status --brief": mode, baseline and sorted results.</summary>
    public void ShowTest(JsonObject? test)
    {
        var results = test.Obj("results");
        var mode = test.Str("mode") ?? results.Str("mode");
        ModeText = mode switch
        {
            "isolated" => L.T("Test.Mode.Isolated"),
            "exclusive" => L.F("Test.Mode.Exclusive", ModeReason(test.Str("mode_reason") ?? results.Str("mode_reason"))),
            _ => "",
        };
        var baseline = results.Obj("baseline");
        BaselineText = baseline == null ? "" : L.F("Test.Baseline", baseline.Long("ok"), baseline.Long("total"));
        var current = State.Status.Obj("strategy").Str("id");
        Results.Clear();
        var rank = 1;
        foreach (var r in results.Objs("results"))
            Results.Add(TestRow.From(rank++, r, current));
        HasResults = Results.Count > 0;
        if (test.Bool("running"))
        {
            IsTesting = true;
            Job.Update(test.Obj("job"));
        }
    }

    public static string ModeReason(string? reason) => reason switch
    {
        "forced" or "engine_not_running" or "no_test_user" or "qnum_out_of_range" or "mark_conflict" or "write_failed"
            or "nft_rejected" or "instance_failed" => L.T("Test.Reason." + reason),
        null or "" => L.T("Test.Reason.unknown"),
        _ => reason,
    };

    [RelayCommand]
    private async Task Select(StrategyItem? item)
    {
        if (item == null || item.IsActive)
            return;
        State.NoteOwnStrategyChange();
        var ok = await Try(() => State.CallAsync("strategy.set", new JsonObject { ["id"] = item.Id }), "Strategies.Err.Set");
        if (ok)
        {
            ShowMessage(MessageKind.Success, L.F("Strategies.Selected", item.Name), L.T("Strategies.SelectedText"));
            await RefreshAllAsync();
        }
    }

    [RelayCommand]
    private async Task StartTest()
    {
        var text = QuickMode ? L.T("Test.ConfirmQuick") : L.T("Test.ConfirmFull");
        if (ForceExclusive)
            text += "\n\n" + L.T("Test.ConfirmExclusive");
        if (!await State.Platform.ConfirmAsync(L.T("Test.Start"), text, L.T("Test.StartButton")))
            return;
        IsTesting = true;
        HasResults = false;
        Results.Clear();
        State.NoteOwnStrategyChange();
        await Try(async () =>
        {
            var args = new JsonObject { ["quick"] = QuickMode, ["apply_if_better"] = ApplyIfBetter, ["exclusive"] = ForceExclusive };
            var (_, job) = await State.RunJobAsync("test.start", args, Job.Update);
            var test = await State.CallAsync("test.status", new JsonObject { ["brief"] = true });
            ShowTest(test);
            ResultText = TestSummary.Text(job, test.Obj("results"));
        }, "Test.Err.Start", busy: false);
        IsTesting = false;
        await RefreshAllAsync();
    }

    [RelayCommand]
    private async Task StopTest()
    {
        await Try(async () =>
        {
            var r = await State.CallAsync("test.stop");
            ShowMessage(MessageKind.Info, L.T("Test.Stopping"), r.Str("state") == "restored" ? L.T("Test.Restored") : L.T("Test.StoppingText"));
        }, "Test.Err.Stop");
    }

    [RelayCommand]
    private async Task ApplyResult(TestRow? row)
    {
        if (row == null)
            return;
        State.NoteOwnStrategyChange();
        var ok = await Try(() => State.CallAsync("test.apply", new JsonObject { ["id"] = row.Id }), "Strategies.Err.Set");
        if (ok)
        {
            ShowMessage(MessageKind.Success, L.F("Strategies.Selected", row.Name), L.T("Strategies.SelectedText"));
            await RefreshAllAsync();
        }
    }

    [RelayCommand]
    private Task ApplyBest() => ApplyResult(Results.FirstOrDefault(r => r.CanApply));

    partial void OnSelectedChanged(StrategyItem? value)
    {
        HasSelection = value != null;
        HasCheck = false;
        _ = ShowSelectedAsync(value);
    }

    private async Task ShowSelectedAsync(StrategyItem? item)
    {
        if (item == null)
            return;
        await Try(async () =>
        {
            var r = await State.CallAsync("strategy.show", new JsonObject { ["id"] = item.Id });
            EditorText = r.Str("text") ?? "";
            IsEditable = item.IsUser;
            EditorName = item.IsUser ? item.Id : Validation.ToUserStrategyId(item.Id);
            SelectedInfo = L.F("Strategies.Info", item.SourceLabel, Ports(r.Obj("ports")), r.Strings("dependencies") is { Count: > 0 } d ? L.List(d) : "—");
        }, "Strategies.Err.Show", busy: false);
    }

    public static string Ports(JsonObject? ports)
    {
        string Join(string key) => ports.Arr(key).Count == 0 ? L.T("Common.None") : L.List(ports.Arr(key).Select(p => p?.ToString()));
        return L.F("Strategies.Ports", Join("tcp"), Join("udp"));
    }

    [RelayCommand]
    private void NewStrategy()
    {
        Selected = null;
        EditorName = "user-my-strategy";
        EditorText = "# " + L.T("Strategies.NewComment") + "\n--filter-tcp=443 ${hostlists} --dpi-desync=fake,multidisorder --dpi-desync-split-pos=midsld\n";
        IsEditable = true;
        HasSelection = true;
        SelectedInfo = L.T("Strategies.NewInfo");
    }

    /// <summary>Saves an own strategy (the service builds and checks the arguments) and, when it is the current one,
    /// runs "check" to show what the engine says.</summary>
    [RelayCommand]
    private async Task SaveAndCheck()
    {
        var id = EditorName.Trim();
        if (!Validation.IsUserStrategyId(id))
        {
            ShowMessage(MessageKind.Warning, L.T("Strategies.BadName"), L.T("Strategies.BadNameText"));
            return;
        }
        if (System.Text.Encoding.UTF8.GetByteCount(EditorText) > MaxStrategyBytes)
        {
            ShowMessage(MessageKind.Warning, L.T("Strategies.TooLarge"), L.T("Err.too_large"));
            return;
        }
        HasCheck = false;
        CheckWarnings.Clear();
        IsBusy = true;
        try
        {
            var saved = await State.CallAsync("strategy.save", new JsonObject { ["id"] = id, ["text"] = EditorText });
            ArgsText = string.Join("\n", saved.Strings("args"));
            PortsText = Ports(saved.Obj("ports"));
            EngineOutput = "";
            foreach (var w in saved.Strings("warnings"))
                CheckWarnings.Add(new WarningItem(UiText.Warning(w, saved), _ => Task.CompletedTask));
            (CheckKind, CheckText) = (K.Ok, L.T("Strategies.Saved"));
            if (State.Status.Obj("strategy").Str("id") == id)
            {
                var check = await State.CallAsync("check");
                EngineOutput = check.Obj("dry_run").Str("output") ?? "";
                (CheckKind, CheckText) = (K.Ok, L.T("Strategies.CheckOk"));
            }
            var items = await State.CallAsync("items");
            FillStrategies(items);
            Selected = Strategies.FirstOrDefault(s => s.Id == id);
            HasCheck = true; // after the selection: selecting a strategy hides the previous check
        }
        catch (ZaprettCallException e)
        {
            (CheckKind, CheckText) = (K.Fail, e.Text);
            EngineOutput = e.Reply.Obj("dry_run").Str("output") ?? "";
            ArgsText = string.Join("\n", e.Reply.Strings("args"));
            PortsText = "";
            HasCheck = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { IsUser: true } item)
            return;
        if (!await State.Platform.ConfirmAsync(L.T("Strategies.Delete"), L.F("Strategies.DeleteText", item.Name), L.T("Common.Delete")))
            return;
        var ok = await Try(() => State.CallAsync("strategy.delete", new JsonObject { ["id"] = item.Id }), "Strategies.Err.Delete");
        if (ok)
        {
            Selected = null;
            HasSelection = false;
            await LoadAsync();
        }
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            await State.RefreshAsync();
            FillStrategies(await State.CallAsync("items"));
        }
        catch (ZaprettCallException)
        {
            // shown by the shell
        }
    }
}
