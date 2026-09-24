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

public enum WizardStep
{
    Intro = 0,
    Services = 1,
    Conflicts = 2,
    Apply = 3,
    Fix = 4,
    Done = 5,
}

/// <summary>A step in the left rail of the wizard.</summary>
public sealed partial class RailStep(int number, string title) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;

    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial bool IsDone { get; set; }

    public string Kind => IsCurrent ? K.Info : IsDone ? K.Ok : K.None;
    public string Mark => IsDone ? "✓" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public double Weight => IsCurrent ? 1.0 : 0.72;

    partial void OnIsCurrentChanged(bool value) => Refresh();

    partial void OnIsDoneChanged(bool value) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Mark));
        OnPropertyChanged(nameof(Weight));
    }
}

/// <summary>A conflicting program found by "conflicts" (ARCHITECTURE-WIN §8).</summary>
public sealed record ConflictItem(string Name, string SeverityLabel, string Kind, string Detail, string Fix, string Where = "")
{
    public static ConflictItem From(JsonObject o) => new(
        o.Str("name") ?? o.Str("id") ?? "", UiText.Severity(o.Str("severity")), UiText.SeverityKind(o.Str("severity")),
        ConflictText.Detail(o), ConflictText.Fix(o), ConflictText.Where(o));

    public bool HasFix => Fix.Length > 0;

    public bool HasWhere => Where.Length > 0;
}

/// <summary>One checked site of "diagnose" (§15.4).</summary>
public sealed record DiagnoseRow(string Host, string Label, string Kind, string Dns, string Detail, bool Spoofed)
{
    public static DiagnoseRow From(JsonObject t)
    {
        var v = UiText.Verdict(t.Str("verdict"));
        var dns = t.Obj("dns");
        string List(string key) => dns.Strings(key) is { Count: > 0 } l ? L.List(l) : "—";
        return new DiagnoseRow(t.Str("host") ?? t.Str("url") ?? "", v.Label, v.Kind,
            dns == null ? "—" : L.F("Diag.DnsText", List("system"), List("doh")), t.Str("detail") ?? "", dns.Bool("spoofed"));
    }
}

/// <summary>
/// First-run wizard (plan §4.2): what it is → services → conflicts → apply, start and live check → if something does
/// not open: how the provider blocks and automatic strategy selection → result and autostart.
/// </summary>
public sealed partial class WizardViewModel : PageViewModel
{
    public WizardViewModel(AppState state, INavigator? nav) : base(state, nav)
    {
        var titles = new[] { "Wizard.Rail.Intro", "Wizard.Rail.Services", "Wizard.Rail.Conflicts", "Wizard.Rail.Apply", "Wizard.Rail.Fix", "Wizard.Rail.Done" };
        for (var i = 0; i < titles.Length; i++)
            Rail.Add(new RailStep(i + 1, L.T(titles[i])));
        UpdateRail();
    }

    public ObservableCollection<RailStep> Rail { get; } = [];

    private void UpdateRail()
    {
        foreach (var r in Rail)
        {
            r.IsCurrent = r.Number == StepNumber;
            r.IsDone = r.Number < StepNumber;
        }
    }

    /// <summary>Raised when the wizard is finished or skipped.</summary>
    public event EventHandler? Finished;

    [ObservableProperty] public partial WizardStep Step { get; set; } = WizardStep.Intro;
    [ObservableProperty] public partial bool HasBlockingConflicts { get; set; }
    [ObservableProperty] public partial bool ConflictsLoaded { get; set; }
    [ObservableProperty] public partial string ApplyNotes { get; set; } = "";
    [ObservableProperty] public partial bool ApplyFinished { get; set; }
    [ObservableProperty] public partial bool ApplyFailed { get; set; }
    [ObservableProperty] public partial string FailedText { get; set; } = "";
    [ObservableProperty] public partial string DiagnoseTitle { get; set; } = "";
    [ObservableProperty] public partial string DiagnoseAdvice { get; set; } = "";
    [ObservableProperty] public partial string DiagnoseKind { get; set; } = K.None;
    [ObservableProperty] public partial bool HasDiagnose { get; set; }
    [ObservableProperty] public partial bool CanTurnOnDns { get; set; }
    [ObservableProperty] public partial string TestResultText { get; set; } = "";
    [ObservableProperty] public partial bool HasTestResult { get; set; }
    [ObservableProperty] public partial bool IsAutostart { get; set; }
    [ObservableProperty] public partial string DoneTitle { get; set; } = "";
    [ObservableProperty] public partial string DoneText { get; set; } = "";
    [ObservableProperty] public partial string DoneKind { get; set; } = K.Ok;

    public ObservableCollection<ServiceChoice> Services { get; } = [];
    public ObservableCollection<ConflictItem> Conflicts { get; } = [];
    public ObservableCollection<StepItem> ApplySteps { get; } = [];
    public ObservableCollection<ServiceTile> Results { get; } = [];
    public ObservableCollection<DiagnoseRow> DiagnoseRows { get; } = [];
    public JobProgress Job { get; } = new();

    public int StepNumber => (int)Step + 1;
    public int StepCount => 6;
    public string StepCaption => L.F("Wizard.StepOf", StepNumber, StepCount);
    public bool IsIntro => Step == WizardStep.Intro;
    public bool IsServices => Step == WizardStep.Services;
    public bool IsConflicts => Step == WizardStep.Conflicts;
    public bool IsApply => Step == WizardStep.Apply;
    public bool IsFix => Step == WizardStep.Fix;
    public bool IsDone => Step == WizardStep.Done;
    public bool CanGoBack => Step is WizardStep.Services or WizardStep.Conflicts;
    public bool NoConflicts => ConflictsLoaded && Conflicts.Count == 0;
    public bool CanSkip => Step is WizardStep.Intro or WizardStep.Services;
    public bool ShowNext => Step != WizardStep.Done;
    public bool CanNext => !IsBusy && (Step != WizardStep.Apply || ApplyFinished);
    public bool ShowFixHint => ApplyFinished && ApplyFailed;

    /// <summary>Code of the error that stopped the apply step (null: the sites were checked, some did not open).</summary>
    [ObservableProperty] public partial string? ApplyErrorCode { get; set; }

    /// <summary>Advice under the failure: missing rights need a new sign-in, not another strategy.</summary>
    public string FixHintText => ApplyErrorCode == "access_denied" ? L.T("Wizard.FixHint.AccessDenied") : L.T("Wizard.FixHint");

    /// <summary>"Find out and fix" (strategy selection) makes no sense when the service refused for lack of rights.</summary>
    public bool CanGoFix => ApplyErrorCode != "access_denied";

    partial void OnApplyErrorCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(FixHintText));
        OnPropertyChanged(nameof(CanGoFix));
    }
    public bool ShowAllGood => ApplyFinished && !ApplyFailed;

    public string NextLabel => Step switch
    {
        WizardStep.Intro => L.T("Wizard.Begin"),
        WizardStep.Conflicts => L.T("Wizard.ApplyStart"),
        _ => L.T("Common.Next"),
    };

    public IReadOnlyList<string> SelectedReferences => Services.Where(s => s.IsSelected && s.IsSelectable).Select(s => s.Reference).ToList();

    partial void OnStepChanged(WizardStep value)
    {
        _focusForced = true;
        foreach (var name in new[] { nameof(StepNumber), nameof(StepCaption), nameof(IsIntro), nameof(IsServices), nameof(IsConflicts),
                     nameof(IsApply), nameof(IsFix), nameof(IsDone), nameof(CanGoBack), nameof(CanSkip), nameof(ShowNext), nameof(CanNext),
                     nameof(NextLabel) })
            OnPropertyChanged(name);
        HasMessage = false;
        UpdateRail();
        RequestPrimaryFocus();
    }

    /// <summary>The page is shown: the first step also gets its main button focused (once it is enabled).</summary>
    public void RequestFocusOnStart()
    {
        _focusForced = true;
        RequestPrimaryFocus();
    }

    /// <summary>
    /// Asks the page to put the keyboard focus on the main button of the step ("Next", "Apply and start", "Finish"),
    /// so Enter goes forward. Forced after every step change (waits while the button is disabled); after the button
    /// comes back from a busy state the page moves the focus only if nothing has it (a disabled button loses focus).
    /// </summary>
    public event EventHandler<bool>? PrimaryFocusRequested;

    private bool _focusForced;
    private bool _lastCanNext;

    private void RequestPrimaryFocus()
    {
        var ready = IsDone || CanNext;
        if (ready && (_focusForced || !_lastCanNext))
        {
            var forced = _focusForced;
            _focusForced = false;
            PrimaryFocusRequested?.Invoke(this, forced);
        }
        _lastCanNext = ready;
    }

    partial void OnConflictsLoadedChanged(bool value) => OnPropertyChanged(nameof(NoConflicts));

    partial void OnApplyFinishedChanged(bool value) => NotifyApplyState();

    partial void OnApplyFailedChanged(bool value) => NotifyApplyState();

    private void NotifyApplyState()
    {
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(ShowFixHint));
        OnPropertyChanged(nameof(ShowAllGood));
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy))
            OnPropertyChanged(nameof(CanNext));
        else if (e.PropertyName == nameof(CanNext))
            RequestPrimaryFocus();
    }

    public override async Task LoadAsync()
    {
        if (State.Presets == null || State.Items.Count == 0)
            await Try(() => State.RefreshPresetsAsync(), "Wizard.Err.Presets");
        FillServices();
    }

    public void FillServices()
    {
        var choices = ServiceChoice.FromPresets(State.Presets, State.Items);
        ServiceChoice.Preselect(choices, State.Presets);
        Services.Clear();
        foreach (var c in choices)
            Services.Add(c);
    }

    [RelayCommand]
    private async Task Next()
    {
        switch (Step)
        {
            case WizardStep.Intro:
                if (Services.Count == 0)
                    await LoadAsync();
                Step = WizardStep.Services;
                break;
            case WizardStep.Services:
                if (SelectedReferences.Count == 0)
                {
                    ShowMessage(MessageKind.Warning, L.T("Wizard.NothingSelected"), L.T("Wizard.NothingSelectedText"));
                    return;
                }
                Step = WizardStep.Conflicts;
                await LoadConflictsAsync();
                break;
            case WizardStep.Conflicts:
                Step = WizardStep.Apply;
                await ApplyAsync();
                break;
            case WizardStep.Apply:
            case WizardStep.Fix:
                PrepareDone();
                Step = WizardStep.Done;
                // the switch is turned on once its page is shown (a switch created already on is drawn pale)
                await Task.Delay(100);
                IsAutostart = true;
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
            Step = Step - 1;
    }

    [RelayCommand]
    private void Skip() => Complete();

    [RelayCommand]
    public async Task LoadConflictsAsync()
    {
        ConflictsLoaded = false;
        Conflicts.Clear();
        await Try(async () =>
        {
            var reply = await State.CallAsync("conflicts");
            foreach (var item in reply.Objs("items"))
                Conflicts.Add(ConflictItem.From(item));
        }, "Wizard.Err.Conflicts");
        HasBlockingConflicts = Conflicts.Any(c => c.Kind == K.Fail);
        ConflictsLoaded = true;
    }

    /// <summary>Enables the lists, starts the bypass and checks the sites; decides between "done" and "fix".</summary>
    public async Task ApplyAsync()
    {
        ApplySteps.Clear();
        var lists = new StepItem(L.T("Wizard.Step.Lists"));
        var start = new StepItem(L.T("Wizard.Step.Start"));
        var check = new StepItem(L.T("Wizard.Step.Check"));
        ApplySteps.Add(lists);
        ApplySteps.Add(start);
        ApplySteps.Add(check);
        ApplyFinished = false;
        ApplyFailed = false;
        ApplyErrorCode = null;
        ApplyNotes = "";
        Results.Clear();
        IsBusy = true;
        try
        {
            lists.State = "running";
            var refs = SelectedReferences;
            var applied = await State.CallAsync("wizard.apply", new JsonObject { ["services"] = refs.ToJsonArray() });
            lists.State = "done";
            ApplyNotes = Notes(applied, State.Presets, await SourceListAsync());

            start.State = "running";
            await State.CallAsync("start");
            start.State = "done";

            check.State = "running";
            var ids = refs.Select(r => r.Split(':')[0]).ToList();
            var (_, job) = await State.RunJobAsync("probe", new JsonObject { ["services"] = ids.ToJsonArray() }, j => check.Detail = L.F("Common.Percent", j.Long("progress")));
            var probe = (await State.CallAsync("probe.status")).Obj("probe");
            State.Probe = probe;
            check.Detail = "";
            var tiles = probe.Objs("services").Select(s => ServiceTile.FromProbe(s, State.Presets)).ToList();
            foreach (var t in tiles)
                Results.Add(t);
            check.State = job?.Str("state") is null or "done" ? "done" : "failed";
            var failed = tiles.Where(t => t.Kind is K.Fail or K.Warn).ToList();
            if (tiles.Count == 0)
                check.Detail = L.T("Wizard.NoTargets");
            if (failed.Count > 0)
            {
                FailedText = L.F("Wizard.Failed", L.List(failed.Select(f => f.Name)));
                ApplyFailed = true;
            }
        }
        catch (ZaprettCallException e)
        {
            foreach (var s in ApplySteps.Where(s => s.State == "running"))
            {
                s.State = "failed";
                s.Detail = e.Text;
            }
            ShowError(L.T("Wizard.Err.Apply"), e);
            ApplyErrorCode = e.Code;
            ApplyFailed = true;
            FailedText = e.Text;
        }
        finally
        {
            IsBusy = false;
            ApplyFinished = true;
        }
        await RefreshQuietAsync();
    }

    /// <summary>The subscriptions with their titles, for the notes; without them the notes show the names (ids).</summary>
    private async Task<JsonObject?> SourceListAsync()
    {
        try
        {
            return await State.CallAsync("sources.list");
        }
        catch (ZaprettCallException)
        {
            return null;
        }
    }

    /// <summary>Title of a subscription in the interface language (as on the "Subscriptions" tab), or its name.</summary>
    public static string SourceTitle(string name, JsonObject? sourceList) =>
        L.Pick(sourceList.Objs("sources").FirstOrDefault(s => s.Str("name") == name), "title") is { Length: > 0 } title ? title : name;

    /// <summary>Explains the extra results of wizard.apply: skipped services, variants, subscriptions, job errors.</summary>
    public static string Notes(JsonObject reply, JsonObject? presets, JsonObject? sourceList = null)
    {
        var notes = new List<string>();
        var skipped = reply.Objs("skipped").Select(s => ServiceTile.ServiceName(s.Str("id"), null, presets)).ToList();
        if (skipped.Count > 0)
            notes.Add(L.F("Wizard.Note.Skipped", L.List(skipped)));
        var variants = reply.Obj("variants");
        if (variants != null)
        {
            var chosen = variants.Where(kv => kv.Value is JsonValue).Select(kv => $"{ServiceTile.ServiceName(kv.Key, null, presets)} — {kv.Value}").ToList();
            if (chosen.Count > 0)
                notes.Add(L.F("Wizard.Note.Variants", L.List(chosen)));
        }
        var sources = reply.Strings("sources").Select(n => SourceTitle(n, sourceList)).ToList();
        if (sources.Count > 0)
            notes.Add(L.F("Wizard.Note.Sources", L.List(sources)));
        var off = reply.Strings("sources_disabled").Select(n => SourceTitle(n, sourceList)).ToList();
        if (off.Count > 0)
            notes.Add(L.F("Wizard.Note.SourcesOff", L.List(off)));
        if (reply.Obj("job_error") is { } je)
            notes.Add(L.F("Wizard.Note.JobError", UiText.Error(je.Str("code"), je.Str("message"))));
        // the title alone ("the settings did not pass the check") says nothing: the explanation goes with it
        foreach (var w in reply.Strings("warnings"))
        {
            var warning = UiText.Warning(w);
            notes.Add(warning.Text.Length > 0 ? L.F("Fmt.WarningNote", warning.Title, warning.Text) : warning.Title);
        }
        return string.Join("\n", notes);
    }

    [RelayCommand]
    private void GoFix() => Step = WizardStep.Fix;

    [RelayCommand]
    private async Task Diagnose()
    {
        HasDiagnose = false;
        DiagnoseRows.Clear();
        await Try(async () =>
        {
            var ids = Results.Where(r => r.Kind is K.Fail or K.Warn).Select(r => r.Id).Where(Validation.IsId).ToList();
            var args = ids.Count > 0 ? new JsonObject { ["services"] = ids.ToJsonArray() } : null;
            var (_, job) = await State.RunJobAsync("diagnose", args, Job.Update);
            Job.Update(null);
            if (job != null && job.Str("state") != "done")
            {
                ShowMessage(MessageKind.Error, L.T("Wizard.Err.Diagnose"), job.Str("message") ?? UiText.JobState(job.Str("state")));
                return;
            }
            var diag = (await State.CallAsync("diagnose.status")).Obj("diagnose");
            ShowDiagnose(diag);
        }, "Wizard.Err.Diagnose");
    }

    private void ShowDiagnose(JsonObject? diag)
    {
        var summary = DiagnoseSummary.From(diag);
        DiagnoseTitle = summary.Conclusion;
        DiagnoseAdvice = summary.Advice;
        DiagnoseKind = summary.Kind;
        foreach (var t in diag.Objs("targets"))
            DiagnoseRows.Add(DiagnoseRow.From(t));
        var dns = State.Dns ?? State.Status.Obj("dns");
        CanTurnOnDns = summary.Verdict is "dns_spoof" or "http_block" && !dns.Bool("encrypted") && dns.Bool("supported", true);
        HasDiagnose = true;
    }

    [RelayCommand]
    private async Task TurnOnDns()
    {
        await Try(async () =>
        {
            var (reply, job) = await State.RunJobAsync("dns.setup", new JsonObject { ["enable"] = true }, Job.Update);
            Job.Update(null);
            if (job != null && job.Str("state") != "done")
                throw new ZaprettCallException(job.Str("error") ?? "failed", job.Str("message"));
            State.Dns = (await State.CallAsync("dns.status")).Obj("dns");
            CanTurnOnDns = false;
            ShowMessage(MessageKind.Success, L.T("Dns.On"), reply.Bool("changed", true) ? L.T("Dns.OnText") : L.T("Dns.AlreadyOn"));
        }, "Dns.Err");
    }

    /// <summary>Quick automatic selection that applies a strictly better strategy; then the sites are checked again.</summary>
    [RelayCommand]
    private async Task AutoSelect()
    {
        if (!await ConfirmSelectionDespiteConflictAsync())
            return;
        HasTestResult = false;
        await Try(async () =>
        {
            State.NoteOwnStrategyChange();
            var (_, job) = await State.RunJobAsync("test.start", new JsonObject { ["quick"] = true, ["apply_if_better"] = true }, Job.Update);
            Job.Update(null);
            var test = await State.CallAsync("test.status", new JsonObject { ["brief"] = true });
            var note = TestSummary.ConflictNote(job, test.Obj("results"));
            TestResultText = note.Length > 0 ? TestSummary.Text(job, test.Obj("results")) + "\n\n" + note : TestSummary.Text(job, test.Obj("results"));
            HasTestResult = true;
            await RecheckAsync();
        }, "Wizard.Err.Test");
        await RefreshQuietAsync();
    }

    private async Task RecheckAsync()
    {
        var ids = Results.Select(r => r.Id).Where(Validation.IsId).ToList();
        await State.RunJobAsync("probe", ids.Count > 0 ? new JsonObject { ["services"] = ids.ToJsonArray() } : null, null);
        var probe = (await State.CallAsync("probe.status")).Obj("probe");
        State.Probe = probe;
        Results.Clear();
        foreach (var s in probe.Objs("services"))
            Results.Add(ServiceTile.FromProbe(s, State.Presets));
        var failed = Results.Where(t => t.Kind is K.Fail or K.Warn).ToList();
        ApplyFailed = failed.Count > 0;
        FailedText = ApplyFailed ? L.F("Wizard.Failed", L.List(failed.Select(f => f.Name))) : "";
    }

    [RelayCommand]
    private Task Recheck() => Try(RecheckAsync, "Wizard.Err.Check");

    private void PrepareDone()
    {
        if (ApplyFailed)
            (DoneKind, DoneTitle, DoneText) = (K.Warn, L.T("Wizard.Done.PartTitle"), L.T("Wizard.Done.PartText"));
        else
            (DoneKind, DoneTitle, DoneText) = (K.Ok, L.T("Wizard.Done.Title"), L.T("Wizard.Done.Text"));
    }

    /// <summary>Saves the autostart choice and closes the wizard.</summary>
    [RelayCommand]
    private async Task Finish()
    {
        var ok = await Try(() => State.SetAutostartAsync(IsAutostart), "Wizard.Err.Autostart");
        if (!ok)
            return;
        Complete();
        await RefreshQuietAsync();
    }

    private void Complete()
    {
        State.Prefs.MarkWizardDone(WizardDecision.InstallId(State.Status));
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshQuietAsync()
    {
        try
        {
            await State.RefreshAsync();
        }
        catch (ZaprettCallException)
        {
            // shown by the shell
        }
    }
}

/// <summary>Conclusion of "diagnose" (§15.4): "N of M open; for the others — …" plus advice.</summary>
public sealed record DiagnoseSummary(string Verdict, string Kind, string Conclusion, string Advice, string Counts)
{
    public static DiagnoseSummary From(JsonObject? diag)
    {
        if (diag == null)
            return new DiagnoseSummary("none", K.None, L.T("Diag.NotYet"), "", "");
        if (diag.Arr("targets").Count == 0)
            return new DiagnoseSummary("none", K.None, L.T("Diag.NothingToCheck"), "", "");
        var summary = diag.Obj("summary");
        var verdict = summary.Str("verdict") ?? "unknown";
        var info = UiText.Verdict(verdict);
        var counts = summary.Obj("counts") ?? [];
        var parts = counts.Where(kv => counts.Long(kv.Key) > 0).Select(kv => L.F("Fmt.CountItem", UiText.Verdict(kv.Key).Label, counts.Long(kv.Key))).ToList();
        var okCount = counts.Long("ok");
        var total = counts.Sum(kv => Math.Max(0, counts.Long(kv.Key)));
        var conclusion = verdict == "ok" ? L.T("Diag.Conclusion.Ok")
            : okCount > 0 ? L.F("Diag.Conclusion.Some", okCount, total, info.Label)
            : L.F("Diag.Conclusion.All", info.Label);
        var kind = verdict == "ok" ? K.Ok : info.Kind;
        return new DiagnoseSummary(verdict, kind, conclusion, info.Advice,
            parts.Count > 0 ? L.F("Diag.Counts", string.Join(L.T("Fmt.GroupSep"), parts)) : "");
    }
}

/// <summary>Result text of an automatic selection job (router contract §10, §14.3 applied).</summary>
public static class TestSummary
{
    public static string Text(JsonObject? job, JsonObject? results)
    {
        var state = job.Str("state");
        if (state == "cancelled")
            return L.T("Test.Cancelled");
        if (state == "failed")
            return L.F("Test.Failed", job.Str("message") ?? "");
        var applied = job.Obj("result").Str("applied") ?? results.Str("applied");
        if (applied != null)
            return L.F("Test.Applied", applied);
        var best = results.Objs("results").FirstOrDefault(r => r.Str("status") is null or "done");
        if (best == null || best.Long("ok") == 0)
            return L.T("Test.NoneHelped");
        return L.F("Test.NoBetter", L.Pick(best, "name") ?? best.Str("id"), best.Long("ok"), best.Long("total"));
    }

    /// <summary>
    /// "Selected while X interfered": the job result (conflict_blocking, conflicts_blocking — a snapshot at the start)
    /// or the saved results (results.conflicts_blocking); empty when nothing interfered.
    /// </summary>
    public static string ConflictNote(JsonObject? job, JsonObject? results)
    {
        var result = job.Obj("result");
        var names = BlockingConflict.All(result, "conflicts_blocking").Concat(BlockingConflict.All(results, "conflicts_blocking"))
            .Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0 && result.Bool("conflict_blocking"))
            names.Add(L.T("Conflict.UnknownProgram"));
        return names.Count == 0 ? "" : L.F("Test.ConflictNote", L.List(names));
    }
}
