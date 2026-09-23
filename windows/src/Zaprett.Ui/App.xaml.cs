using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Zaprett.Ipc;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;
using Zaprett.Ui.Platform;

namespace Zaprett.Ui;

/// <summary>Command line of the interface.</summary>
public sealed record StartOptions(bool Fake, string? ScreenshotDir, string? Theme, string? Language, bool Minimized, string? Page = null)
{
    public static StartOptions Parse(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            var i = args.ToList().IndexOf(name);
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }
        return new StartOptions(
            ClientFactory.IsFake(args, Environment.GetEnvironmentVariable),
            Value("--screenshot"),
            Value("--theme") is "light" or "dark" or "system" ? Value("--theme") : null,
            Value("--lang") is { } lang ? L.Normalize(lang) : null,
            // --tray: started by the Run key of the installer, stays in the notification area
            args.Contains("--minimized") || args.Contains("--tray"),
            Value("--page"));
    }
}

public partial class App : Application
{
    private MainWindow? _window;
    private readonly CancellationTokenSource _cts = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // an unexpected exception must not close the tray application silently: log it next to the prefs
            Log("unhandled: " + e.Exception);
            e.Handled = true;
        };
    }

    public static new App? Current => Application.Current as App;

    public IServiceProvider Services { get; private set; } = null!;

    public StartOptions Options { get; private set; } = new(false, null, null, null, false);

    public static T Get<T>() where T : notnull => Current!.Services.GetRequiredService<T>();

    /// <summary>Where the interface keeps its own settings: none for screenshots, a separate file in fake mode.</summary>
    public static string? PrefsPath(StartOptions options) => options.ScreenshotDir != null ? null
        : options.Fake ? Path.Combine(Path.GetDirectoryName(UiPrefs.DefaultPath())!, "ui-fake.json")
        : UiPrefs.DefaultPath();

    /// <summary>The interface language of this start (--lang, else the saved choice), known before the app exists.</summary>
    public static string StartLanguage(IReadOnlyList<string> args)
    {
        var options = StartOptions.Parse(args);
        return options.Language ?? UiPrefs.Load(PrefsPath(options)).Language;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToList();
        Options = StartOptions.Parse(argv);
        var prefs = UiPrefs.Load(PrefsPath(Options));
        L.SetLanguage(Options.Language ?? prefs.Language);
        // an elevated interface is invisible to Narrator and UI automation of the user (UIPI lets them see only the
        // caption buttons); it happens when an elevated process such as the installer starts it
        if (Environment.IsPrivilegedProcess)
            Log("start: the interface runs elevated; screen readers and UI automation of the user cannot see its content");

        var services = new ServiceCollection();
        services.AddSingleton(prefs);
        services.AddSingleton<IZaprettClient>(_ => ClientFactory.Create(argv, Environment.GetEnvironmentVariable));
        services.AddSingleton<WinPlatform>();
        services.AddSingleton<IUiPlatform>(sp => sp.GetRequiredService<WinPlatform>());
        services.AddSingleton<Navigator>();
        services.AddSingleton<INavigator>(sp => sp.GetRequiredService<Navigator>());
        services.AddSingleton<AppState>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<WizardViewModel>();
        services.AddTransient<ServicesViewModel>();
        services.AddTransient<StrategiesViewModel>();
        services.AddTransient<ListsViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddTransient<SettingsViewModel>();
        Services = services.BuildServiceProvider();

        AppState.Log = Log;
        var state = Get<AppState>();
        _window = new MainWindow(state, Options);
        Get<WinPlatform>().Attach(_window);

        if (Options.ScreenshotDir != null)
        {
            _window.Activate();
            _ = RunScreenshotsAsync(Options.ScreenshotDir);
            return;
        }

        _window.CreateTray();
        if (!Options.Minimized)
            _window.Activate();
        _ = state.RunAsync(_cts.Token);
    }

    private async Task RunScreenshotsAsync(string dir)
    {
        var code = 0;
        try
        {
            await new Screenshotter(_window!, Get<AppState>(), dir).RunAsync();
        }
        catch (Exception e)
        {
            Log("screenshot: " + e);
            code = 3;
        }
        Shutdown(code);
    }

    public void ShowMainWindow() => _window?.DispatcherQueue.TryEnqueue(() => _window.ShowFromTray());

    /// <summary>Closes the interface (the service keeps working).</summary>
    public void Shutdown(int code = 0)
    {
        _cts.Cancel();
        _window?.DisposeTray();
        Environment.ExitCode = code;
        base.Exit();
    }

    public static void Log(string text)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(UiPrefs.DefaultPath())!, "ui.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {text}\n");
        }
        catch (IOException)
        {
            // nowhere to log
        }
        catch (UnauthorizedAccessException)
        {
            // nowhere to log
        }
    }
}

/// <summary>Forwards navigation requests of view models to the current shell (it is rebuilt on language change).</summary>
public sealed class Navigator : INavigator
{
    public Action<string>? Target { get; set; }

    public void Navigate(string page) => Target?.Invoke(page);
}
