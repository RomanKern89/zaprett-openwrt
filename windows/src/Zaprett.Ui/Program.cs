using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Zaprett.Ui;

/// <summary>
/// Entry point. One interface per user session: a second start activates the running window instead of opening
/// another one (the tray icon would otherwise be doubled). Fake and screenshot runs are exempt.
/// </summary>
public static class Program
{
    public const string InstanceKey = "zaprett-ui";

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var exempt = args.Any(a => a.StartsWith("--fake", StringComparison.Ordinal) || a == "--screenshot");
        if (!exempt && !IsFirstInstance())
            return 0;

        // built-in texts of WinUI controls take the language from here, and only if it is set before the start
        Platform.ControlsLanguage.Apply(App.StartLanguage(args));
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return Environment.ExitCode;
    }

    private static string? LaunchArguments(AppActivationArguments e) =>
        e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? launch.Arguments
            : null;

    private static bool IsFirstInstance()
    {
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (instance.IsCurrent)
        {
            // a second start: the Start menu shortcut brings the window; --tray (Run key at sign-in, restart after
            // an update) only finds the running instance and its one icon
            instance.Activated += (_, e) =>
            {
                if (Core.Services.ActivationPolicy.ShowsWindow(LaunchArguments(e)))
                    App.Current?.ShowMainWindow();
                else
                    App.Log("activation: second start with --tray, the window stays as it is");
            };
            return true;
        }
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // the redirection must not run on this STA thread (it would wait for itself); a pool thread does it
        Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(5));
        return false;
    }
}
