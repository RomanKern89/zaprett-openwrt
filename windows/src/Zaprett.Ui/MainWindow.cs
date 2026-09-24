using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;
using Zaprett.Ui.Platform;
using Zaprett.Ui.Views;

namespace Zaprett.Ui;

/// <summary>
/// The only window: Mica (Windows 11) or acrylic (Windows 10) backdrop, own title bar, the shell page, closing to
/// the notification area, and the tray icon that follows the state of the bypass.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppState _state;
    private readonly StartOptions _options;
    private TrayIcon? _tray;
    private ElementTheme _effectiveTheme = ElementTheme.Light;
    private bool _solid;
    private string? _themeSetting;
    private bool _exiting;

    public MainWindow(AppState state, StartOptions options)
    {
        _state = state;
        _options = options;
        Title = "zaprett";
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "zaprett.ico"));
        SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
        PlaceWindow();
        AppWindow.Closing += OnClosing;
        BuildShell(options.Page);
        ApplyTheme(options.Theme ?? state.Prefs.Theme);
        Shell.Loaded += LogStartupTime;
    }

    /// <summary>Start-up time of the window (process start to the first Loaded): measures ReadyToRun choices.</summary>
    private void LogStartupTime(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= LogStartupTime;
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        App.Log($"startup: window loaded {(DateTime.Now - started).TotalMilliseconds:0} ms after process start");
    }

    public ShellPage Shell { get; private set; } = null!;

    public bool IsScreenshotRun => _options.ScreenshotDir != null;

    /// <summary>(Re)creates the shell; used at start and when the language changes.</summary>
    public void BuildShell(string? page)
    {
        (Content as ShellPage)?.Release();
        Shell = new ShellPage(this, _state, App.Get<ShellViewModel>(), App.Get<Navigator>())
        {
            // picks the Chinese (not Japanese) glyph forms and the matching fallback font
            Language = L.Culture.Name,
        };
        Content = Shell;
        SetTitleBar(Shell.TitleBarElement);
        Shell.Start(page);
        Shell.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        if (_themeSetting != null)
            ApplyTheme(_themeSetting);
        UpdateCaptionButtons();
    }

    public void ApplyTheme(string theme)
    {
        _themeSetting = theme;
        var requested = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Shell.RequestedTheme = requested;
        _effectiveTheme = requested != ElementTheme.Default ? requested : SystemTheme;
        // Mica/acrylic follow the Windows theme only: checked on 2026-09-23, a dark interface on a light Windows got a
        // light backdrop and unreadable text. When the themes differ, the window gets the solid base colour instead.
        Shell.RootElement.Background = _solid || _effectiveTheme != SystemTheme ? SolidBase(_effectiveTheme) : null;
        UpdateCaptionButtons();
    }

    public void ApplyLanguage(string language)
    {
        L.SetLanguage(language);
        Platform.ControlsLanguage.Apply(language);
        BuildShell(Shell.CurrentPage.Length > 0 ? Shell.CurrentPage : null);
        _tray?.UpdateMenuTexts();
    }

    private void UpdateCaptionButtons()
    {
        var dark = Shell.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
    }

    /// <summary>For screenshots: a solid background instead of the backdrop, which RenderTargetBitmap cannot see.</summary>
    public void UseSolidBackground()
    {
        _solid = true;
        Shell.RootElement.Background = SolidBase(_effectiveTheme);
    }

    private static ElementTheme SystemTheme =>
        Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>SolidBackgroundFillColorBase of the Fluent theme.</summary>
    private static SolidColorBrush SolidBase(ElementTheme theme) => new(theme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
        : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3));

    public void CreateTray()
    {
        // one icon per process: a second call (whatever path leads here) must not add another
        if (_tray != null)
            return;
        _tray = new TrayIcon(this, _state);
        _tray.Create();
    }

    public void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
            p.Restore();
        Activate();
    }

    public void ExitFromTray()
    {
        _exiting = true;
        App.Current?.Shutdown();
    }

    public void ShowBalloon(string title, string text) => _tray?.ShowBalloon(title, text);

    /// <summary>Size and position at start: fits the work area of the monitor (a 1280×800 screen included), reuses
    /// the last position only while it is on a current monitor. Screenshots always use the reference size.</summary>
    private void PlaceWindow()
    {
        if (IsScreenshotRun)
        {
            AppWindow.Resize(new SizeInt32(WindowPlacement.PreferredWidth, WindowPlacement.PreferredHeight));
            SetMinimumSize(WindowPlacement.DragMinimumWidth, WindowPlacement.DragMinimumHeight);
            return;
        }
        try
        {
            var primary = Monitors.PrimaryWorkArea();
            var scale = Monitors.ScaleAt(primary.CenterX, primary.CenterY);
            var rect = WindowPlacement.Compute(primary, scale, _state.Prefs.Window?.ToRect(), Monitors.AllWorkAreas());
            AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
            // the minimum follows the monitor the window actually opened on
            var area = Monitors.ToPixelRect(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea);
            var (minWidth, minHeight) = WindowPlacement.MinimumSize(area, Monitors.ScaleAt(rect.CenterX, rect.CenterY));
            SetMinimumSize(minWidth, minHeight);
            App.Log($"window: work area {area.Width}x{area.Height}, scale {scale:0.##}, placed {rect.X},{rect.Y} {rect.Width}x{rect.Height}");
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            App.Log("window: placement failed, " + ex.Message);
        }
    }

    private void SetMinimumSize(int width, int height)
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = width;
            p.PreferredMinimumHeight = height;
        }
    }

    /// <summary>Remembers the restored position and size (a maximized or minimized window keeps the previous one).</summary>
    private void SaveWindowPlacement()
    {
        if (IsScreenshotRun || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
            return;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        _state.Prefs.Window = SavedWindow.From(new PixelRect(pos.X, pos.Y, size.Width, size.Height));
        _state.Prefs.Save();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (AppWindow.IsVisible)
            SaveWindowPlacement();
        if (_exiting || IsScreenshotRun || _tray == null || !_state.Prefs.CloseToTray)
        {
            DisposeTray();
            return;
        }
        // the service keeps working; the interface stays in the notification area
        args.Cancel = true;
        AppWindow.Hide();
    }
}
