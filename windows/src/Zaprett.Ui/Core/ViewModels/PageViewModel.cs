using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using K = Zaprett.Ui.Core.Text.Kind;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>Kinds of the page message bar (map to InfoBarSeverity in the view).</summary>
public static class MessageKind
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Success = "success";
    public const string Info = "info";
}

/// <summary>
/// Base of the page view models: access to the shared state, a busy flag, one message bar per page and a
/// helper that turns service errors into human text.
/// </summary>
public abstract partial class PageViewModel : ObservableObject, IDisposable
{
    protected PageViewModel(AppState state, INavigator? nav)
    {
        State = state;
        Nav = nav;
        State.Changed += OnStateChangedHandler;
    }

    public AppState State { get; }

    protected INavigator? Nav { get; }

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasMessage { get; set; }
    [ObservableProperty] public partial string MessageKindValue { get; set; } = MessageKind.Info;
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";

    public void ShowMessage(string kind, string title, string text)
    {
        MessageKindValue = kind;
        MessageTitle = title;
        MessageText = text;
        HasMessage = true;
    }

    [RelayCommand]
    public void CloseMessage() => HasMessage = false;

    public void ShowError(string title, ZaprettCallException e)
    {
        var lines = e.LineErrors;
        ShowMessage(MessageKind.Error, title, lines.Count == 0 ? e.Text : e.Text + "\n" + string.Join("\n", lines));
    }

    /// <summary>Runs an action with the busy flag; a service error is shown in the message bar.
    /// Returns false when the action failed.</summary>
    protected async Task<bool> Try(Func<Task> action, string failTitleKey, bool busy = true)
    {
        if (busy)
            IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (ZaprettCallException e)
        {
            ShowError(L.T(failTitleKey), e);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (busy)
                IsBusy = false;
        }
    }

    /// <summary>Called when the page is shown.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Called on the UI thread when the shared state changed.</summary>
    protected virtual void OnStateChanged()
    {
    }

    private void OnStateChangedHandler(object? sender, EventArgs e) => OnStateChanged();

    [RelayCommand]
    public void Open(string page) => Nav?.Navigate(page);

    public void Dispose()
    {
        State.Changed -= OnStateChangedHandler;
        GC.SuppressFinalize(this);
    }
}

/// <summary>A warning of the status with its action button.</summary>
public sealed partial class WarningItem(WarningText w, Func<string, Task> act) : ObservableObject
{
    public string Code => w.Code;
    public string Title => w.Title;
    public string Text => w.Text;
    public bool IsInfo => w.IsInfo;
    public string Kind => w.IsInfo ? K.Info : K.Warn;
    public bool HasAction => w.Action != null;

    public string ActionLabel => w.Action switch
    {
        "lists" => L.T("Action.OpenLists"),
        "strategies" => L.T("Action.OpenStrategies"),
        "settings" => L.T("Action.OpenSettings"),
        "diagnostics" => L.T("Action.OpenDiagnostics"),
        "restart" => L.T("Action.Restart"),
        _ => "",
    };

    [RelayCommand]
    private Task Act() => w.Action == null ? Task.CompletedTask : act(w.Action);
}

/// <summary>A service tile with the result of the live check: "YouTube ✓ 3/3, 420 ms".</summary>
public sealed class ServiceTile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Result { get; init; }
    public required string Detail { get; init; }
    public required string Glyph { get; init; }
    public string Automation => L.F("Fmt.TargetAutomation", Name, Result, Detail);

    public static ServiceTile FromProbe(JsonObject svc, JsonObject? presets)
    {
        var ok = svc.Long("ok");
        var total = svc.Long("total");
        var kind = UiText.ServiceKind(ok, total);
        var failed = svc.Objs("targets").FirstOrDefault(t => !t.Bool("ok"));
        var detail = kind == K.Ok ? L.F("Home.Tile.AvgTime", UiText.Ms(svc.Long("avg_ms")))
            : kind == K.None ? L.T("Home.Tile.NothingToCheck")
            : failed != null ? UiText.TargetError(failed) : "";
        return new ServiceTile
        {
            Id = svc.Str("id") ?? "",
            Name = ServiceName(svc.Str("id"), L.Pick(svc, "name"), presets),
            Kind = kind,
            Result = total > 0 ? $"{ok}/{total}" : "—",
            Detail = detail,
            Glyph = GlyphFor(kind),
        };
    }

    public static ServiceTile NotChecked(string id, string name) => new()
    {
        Id = id, Name = name, Kind = K.None, Result = "—", Detail = L.T("Home.Tile.NotChecked"), Glyph = GlyphFor(K.None),
    };

    /// <summary>Segoe Fluent Icons: check, warning, error, question.</summary>
    public static string GlyphFor(string kind) => kind switch
    {
        K.Ok => "",
        K.Warn => "",
        K.Fail => "",
        _ => "",
    };

    public static string ServiceName(string? id, string? fallback, JsonObject? presets)
    {
        var preset = presets.Objs("services").FirstOrDefault(s => s.Str("id") == id);
        return (preset != null ? L.Pick(preset, "name") : null) ?? fallback ?? id ?? "";
    }
}

/// <summary>One cell of the monitor history strip (48 last checks).</summary>
public sealed record HistoryCell(string Kind, string Tooltip);

/// <summary>A step of a multi-step action (wizard): pending, running, done, failed, skipped.</summary>
public sealed partial class StepItem(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty] public partial string State { get; set; } = "pending";
    [ObservableProperty] public partial string Detail { get; set; } = "";

    public string Kind => State switch
    {
        "done" => K.Ok,
        "failed" => K.Fail,
        "running" => K.Info,
        _ => K.None,
    };

    public string Glyph => State switch
    {
        "done" => "",
        "failed" => "",
        "running" => "",
        "skipped" => "",
        _ => "",
    };

    public bool IsRunning => State == "running";

    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(IsRunning));
    }
}

/// <summary>Progress of a background job for a progress card.</summary>
public sealed partial class JobProgress : ObservableObject
{
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Kind { get; set; } = K.Info;

    public void Update(JsonObject? job)
    {
        if (job == null)
        {
            IsVisible = false;
            IsRunning = false;
            return;
        }
        var state = job.Str("state") ?? "running";
        IsVisible = true;
        IsRunning = state == "running";
        Title = $"{UiText.JobName(job.Str("name"))} — {UiText.JobState(state)}";
        Message = job.Str("message") ?? "";
        Percent = state == "done" ? 100 : Math.Clamp(job.Long("progress"), 0, 100);
        Kind = state switch
        {
            "done" => K.Ok,
            "failed" => K.Fail,
            "cancelled" => K.Warn,
            _ => K.Info,
        };
    }
}
