using System.Collections.ObjectModel;
using System.Text;
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
/// "Diagnostics": how the provider blocks (verdicts), conflicting programs, the check of the configuration, the
/// service log and a diagnostic report without secrets (copy / save).
/// </summary>
public sealed partial class DiagnosticsViewModel(AppState state, INavigator? nav) : PageViewModel(state, nav)
{
    public ObservableCollection<DiagnoseRow> Rows { get; } = [];
    public ObservableCollection<ConflictItem> Conflicts { get; } = [];
    public ObservableCollection<WarningItem> CheckWarnings { get; } = [];
    public JobProgress Job { get; } = new();

    [ObservableProperty] public partial string SummaryTitle { get; set; } = "";
    [ObservableProperty] public partial string SummaryAdvice { get; set; } = "";
    [ObservableProperty] public partial string SummaryCounts { get; set; } = "";
    [ObservableProperty] public partial string SummaryKind { get; set; } = K.None;
    [ObservableProperty] public partial string SummaryNote { get; set; } = "";
    [ObservableProperty] public partial bool HasRows { get; set; }
    [ObservableProperty] public partial bool CanTurnOnDns { get; set; }
    [ObservableProperty] public partial bool SuggestStrategies { get; set; }
    [ObservableProperty] public partial bool IsDiagnosing { get; set; }
    [ObservableProperty] public partial bool NoConflicts { get; set; }
    [ObservableProperty] public partial string LogText { get; set; } = "";
    [ObservableProperty] public partial string CheckTitle { get; set; } = "";
    [ObservableProperty] public partial string CheckKind { get; set; } = K.None;
    [ObservableProperty] public partial string CheckOutput { get; set; } = "";
    [ObservableProperty] public partial bool HasCheck { get; set; }
    [ObservableProperty] public partial string PlatformText { get; set; } = "";
    [ObservableProperty] public partial bool IsDebug { get; set; }

    private bool _loadingDebug;

    public override async Task LoadAsync()
    {
        await Try(async () =>
        {
            var page = await State.CallAsync("page", new JsonObject { ["name"] = "diagnostics" });
            ShowDiagnose((page.OkPart("diagnose") ?? await State.CallAsync("diagnose.status")).Obj("diagnose"));
            var settings = await State.CallAsync("settings.get");
            _loadingDebug = true;
            IsDebug = (settings.Obj("settings") ?? settings.Obj("config")).Obj("main").Bool("debug");
            _loadingDebug = false;
        }, "Diag.Err.Load");
        PlatformText = Platform(State.Status);
        await Task.WhenAll(LoadConflictsAsync(), LoadLogAsync());
    }

    /// <summary>OS, build, WinDivert and protection settings from status.platform/windivert (ARCHITECTURE-WIN §6).</summary>
    public static string Platform(JsonObject? st)
    {
        var p = st.Obj("platform");
        var w = st.Obj("windivert");
        if (p == null && w == null)
            return "";
        var parts = new List<string>();
        if (p != null)
            parts.Add($"{p.Str("os") ?? "Windows"} {p.Str("build")} {p.Str("arch")}".Trim());
        if (w != null)
            parts.Add(w.Bool("loaded") ? L.F("Diag.WinDivertLoaded", w.Str("version") ?? "?") : L.T("Diag.WinDivertNotLoaded"));
        var foreign = w.Strings("foreign");
        if (foreign.Count > 0)
            parts.Add(L.F("Diag.WinDivertForeign", L.List(foreign)));
        if (p.Bool("hvci"))
            parts.Add(L.T("Diag.Hvci"));
        return string.Join(" · ", parts);
    }

    public void ShowDiagnose(JsonObject? diag)
    {
        var summary = DiagnoseSummary.From(diag);
        SummaryTitle = summary.Conclusion;
        SummaryAdvice = summary.Advice;
        SummaryCounts = summary.Counts;
        SummaryKind = summary.Kind;
        SummaryNote = diag == null ? "" : L.F(diag.Bool("engine_running") ? "Diag.NoteRunning" : "Diag.NoteStopped",
            UiText.Time(diag.Long("finished", diag.Long("started"))));
        Rows.Clear();
        foreach (var t in diag.Objs("targets"))
            Rows.Add(DiagnoseRow.From(t));
        HasRows = Rows.Count > 0;
        var dnsOn = (State.Dns ?? State.Status.Obj("dns")).Bool("encrypted");
        var dns = State.Dns ?? State.Status.Obj("dns");
        CanTurnOnDns = summary.Verdict is "dns_spoof" or "http_block" && !dnsOn && dns.Bool("supported", true);
        SuggestStrategies = summary.Verdict is "tls_block" or "throttle" or "http_block" or "unknown";
    }

    [RelayCommand]
    private async Task Diagnose()
    {
        IsDiagnosing = true;
        await Try(async () =>
        {
            var (_, job) = await State.RunJobAsync("diagnose", null, Job.Update);
            Job.Update(null);
            if (job != null && job.Str("state") != "done")
                ShowMessage(MessageKind.Error, L.T("Wizard.Err.Diagnose"), job.Str("message") ?? UiText.JobState(job.Str("state")));
            ShowDiagnose((await State.CallAsync("diagnose.status")).Obj("diagnose"));
        }, "Wizard.Err.Diagnose", busy: false);
        IsDiagnosing = false;
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

    [RelayCommand]
    public async Task LoadConflictsAsync()
    {
        await Try(async () =>
        {
            var reply = await State.CallAsync("conflicts");
            Conflicts.Clear();
            foreach (var c in reply.Objs("items").Select(ConflictItem.From).OrderBy(c => c.Kind == K.Fail ? 0 : c.Kind == K.Warn ? 1 : 2))
                Conflicts.Add(c);
            NoConflicts = Conflicts.Count == 0;
        }, "Wizard.Err.Conflicts", busy: false);
    }

    [RelayCommand]
    public async Task LoadLogAsync()
    {
        await Try(async () =>
        {
            var reply = await State.CallAsync("log", new JsonObject { ["tail"] = 200 });
            var lines = reply.Strings("lines");
            LogText = lines.Count == 0 ? L.T("Diag.LogEmpty") : Validation.MaskUrls(string.Join("\n", lines));
        }, "Diag.Err.Log", busy: false);
    }

    [RelayCommand]
    private async Task CheckConfig()
    {
        HasCheck = false;
        CheckWarnings.Clear();
        IsBusy = true;
        try
        {
            var r = await State.CallAsync("check");
            (CheckKind, CheckTitle) = (K.Ok, L.T("Diag.CheckOk"));
            CheckOutput = Describe(r);
            foreach (var w in r.Strings("warnings"))
                CheckWarnings.Add(new WarningItem(UiText.Warning(w, r), _ => Task.CompletedTask));
        }
        catch (ZaprettCallException e)
        {
            (CheckKind, CheckTitle) = (K.Fail, L.F("Diag.CheckFail", e.Text));
            CheckOutput = e.Reply == null ? "" : Describe(e.Reply);
        }
        finally
        {
            IsBusy = false;
            HasCheck = true;
        }
    }

    /// <summary>Ports, engine output and arguments of a "check" answer as plain text.</summary>
    public static string Describe(JsonObject r)
    {
        var sb = new StringBuilder();
        if (r.Obj("ports") is { } ports)
            sb.AppendLine(StrategiesViewModel.Ports(ports));
        if (r.Obj("dry_run").Str("output") is { Length: > 0 } output)
        {
            sb.AppendLine();
            sb.AppendLine(L.T("Diag.EngineOutput"));
            sb.AppendLine(output);
        }
        var args = r.Strings("args");
        if (args.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L.T("Diag.EngineArgs"));
            sb.AppendLine(string.Join("\n", args));
        }
        return sb.ToString().TrimEnd();
    }

    partial void OnIsDebugChanged(bool value)
    {
        if (!_loadingDebug)
            _ = SetDebugAsync(value);
    }

    private async Task SetDebugAsync(bool value)
    {
        if (await Try(() => State.CallAsync("settings.set", new JsonObject { ["main"] = new JsonObject { ["debug"] = value } }), "Settings.Err.Save", busy: false))
            return;
        _loadingDebug = true;
        IsDebug = !value;
        _loadingDebug = false;
    }

    /// <summary>Report for support: service "diag" text, status summary, conflicts, diagnosis, log tail. URLs lose their
    /// query (tokens of subscriptions), W-8.</summary>
    public async Task<string> BuildReportAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("zaprett — " + L.T("Diag.Report"));
        sb.AppendLine(DateTimeOffset.Now.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine();
        try
        {
            var diag = await State.CallAsync("diag");
            sb.AppendLine(diag.Str("text") ?? diag.Str("output") ?? "");
        }
        catch (ZaprettCallException e)
        {
            sb.AppendLine("diag: " + e.Text);
        }
        var st = State.Status;
        sb.AppendLine($"status: enabled={st.Bool("enabled")} running={st.Bool("running")} engine={st.Str("engine")} strategy={st.Obj("strategy").Str("id")} mode={st.Str("list_mode")}");
        sb.AppendLine("warnings: " + string.Join(", ", st.Strings("warnings")));
        sb.AppendLine("platform: " + Platform(st));
        sb.AppendLine();
        sb.AppendLine(L.T("Diag.Conflicts") + ":");
        foreach (var c in Conflicts)
            sb.AppendLine($"  [{c.SeverityLabel}] {c.Name}: {c.Detail}");
        if (HasRows)
        {
            sb.AppendLine();
            sb.AppendLine(SummaryTitle);
            foreach (var r in Rows)
                sb.AppendLine($"  {r.Host}: {r.Label}; {r.Dns}; {r.Detail}");
        }
        sb.AppendLine();
        sb.AppendLine(L.T("Diag.Log") + ":");
        sb.AppendLine(LogText);
        return Validation.MaskUrls(sb.ToString());
    }

    [RelayCommand]
    private Task CopyReport() => Try(async () =>
    {
        var text = await BuildReportAsync();
        State.Platform.CopyText(text);
        ShowMessage(MessageKind.Success, L.T("Diag.Copied"), L.T("Diag.CopiedText"));
    }, "Diag.Report");

    [RelayCommand]
    private Task SaveReport() => Try(async () =>
    {
        var text = await BuildReportAsync();
        var path = await State.Platform.SaveTextAsync($"zaprett-report-{DateTime.Now:yyyyMMdd-HHmm}.txt", text);
        if (path != null)
            ShowMessage(MessageKind.Success, L.T("Diag.Saved"), path);
    }, "Diag.Report");
}
