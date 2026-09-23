using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using K = Zaprett.Ui.Core.Text.Kind;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>
/// The window frame: connection to the service (a clear "service is not running" screen with "Start service"),
/// the state pill in the title bar and the tray state.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppState _state;

    public ShellViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += OnStatePropertyChanged;
        _state.Changed += (_, _) => Update();
        Update();
    }

    public AppState State => _state;

    [ObservableProperty] public partial bool IsServiceDown { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; } = true;
    [ObservableProperty] public partial bool IsStarting { get; set; }
    [ObservableProperty] public partial string DownText { get; set; } = "";
    [ObservableProperty] public partial string PillKind { get; set; } = K.None;
    [ObservableProperty] public partial string PillText { get; set; } = "";
    [ObservableProperty] public partial string TrayState { get; set; } = "off";
    [ObservableProperty] public partial string TrayTip { get; set; } = "zaprett";
    [ObservableProperty] public partial bool IsFake { get; set; }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Connection) or nameof(AppState.UnavailableReason))
            Update();
    }

    public void Update()
    {
        IsFake = _state.IsFake;
        IsConnecting = _state.Connection == ConnectionState.Connecting;
        IsServiceDown = _state.Connection == ConnectionState.Unavailable;
        DownText = L.T("Shell.Down.Text");
        (PillKind, PillText, TrayState) = Pill();
        TrayTip = "zaprett — " + PillText;
    }

    /// <summary>Title bar pill and tray icon: off / on / warn / error.</summary>
    public (string Kind, string Text, string Tray) Pill()
    {
        if (_state.Connection == ConnectionState.Unavailable)
            return (K.Fail, L.T("Shell.Pill.Down"), "error");
        if (_state.Connection == ConnectionState.Connecting || _state.Status == null)
            return (K.None, L.T("Shell.Pill.Connecting"), "off");
        var st = _state.Status;
        var monitor = _state.Monitor.Str("state") ?? st.Obj("monitor").Str("state");
        if (st.Bool("running") && monitor is "degraded")
            return (K.Warn, L.T("Shell.Pill.Degraded"), "warn");
        if (st.Bool("running"))
            return (K.Ok, L.T("Shell.Pill.On"), "on");
        if (st.Bool("enabled"))
            return (K.Fail, L.T("Shell.Pill.Broken"), "error");
        return (K.None, L.T("Shell.Pill.Off"), "off");
    }

    /// <summary>Starts the Windows service with UAC (or, in fake mode, makes the fake service reachable).</summary>
    [RelayCommand]
    private async Task StartService()
    {
        IsStarting = true;
        try
        {
            if (_state.Fake is { } fake)
                fake.SimulateServiceStart();
            else if (!await _state.Platform.StartServiceElevatedAsync())
            {
                DownText = L.T("Shell.Down.StartFailed");
                return;
            }
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    await _state.RefreshAsync();
                    return;
                }
                catch (ZaprettCallException)
                {
                    await Task.Delay(500);
                }
            }
            DownText = L.T("Shell.Down.StillDown");
        }
        finally
        {
            IsStarting = false;
        }
    }

    [RelayCommand]
    private async Task Retry()
    {
        try
        {
            await _state.RefreshAsync();
        }
        catch (ZaprettCallException)
        {
            DownText = L.T("Shell.Down.StillDown");
        }
    }
}
