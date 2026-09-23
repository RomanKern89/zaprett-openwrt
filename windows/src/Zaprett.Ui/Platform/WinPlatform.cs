using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;

namespace Zaprett.Ui.Platform;

/// <summary>Desktop services for the view models: UAC start of the service, clipboard, file dialogs, notifications.</summary>
public sealed class WinPlatform : IUiPlatform
{
    private MainWindow? _window;
    private bool _toastsRegistered;
    private bool _toastsFailed;

    public void Attach(MainWindow window) => _window = window;

    /// <summary>"sc start zaprett" elevated through UAC (ShellExecute "runas"). A declined prompt gives false.</summary>
    public async Task<bool> StartServiceElevatedAsync()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = "start zaprett",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(info);
            if (p == null)
                return false;
            await p.WaitForExitAsync();
            // 1056: the service is already running
            return p.ExitCode is 0 or 1056;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return false; // the user declined the UAC prompt
        }
        catch (Win32Exception e)
        {
            App.Log("sc start: " + e.Message);
            return false;
        }
    }

    public void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (COMException e)
        {
            throw new ZaprettCallException("clipboard", L.T("Common.ClipboardError"), null, e);
        }
    }

    public async Task<string?> SaveTextAsync(string suggestedName, string text)
    {
        if (_window == null)
            return null;
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        picker.FileTypeChoices.Add(L.T("Common.TextFile"), [".txt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return null;
        try
        {
            await File.WriteAllTextAsync(file.Path, text.Replace("\n", "\r\n", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ZaprettCallException("write_failed", L.F("Common.FileError", e.Message), null, e);
        }
        return file.Path;
    }

    public async Task<string?> OpenTextAsync()
    {
        if (_window == null)
            return null;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".lst");
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return null;
        try
        {
            if (new FileInfo(file.Path).Length > 1024 * 1024)
                throw new ZaprettCallException("too_large", L.T("Common.FileTooLarge"));
            return await File.ReadAllTextAsync(file.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ZaprettCallException("write_failed", L.F("Common.FileError", e.Message), null, e);
        }
    }

    /// <summary>Windows notification; unpackaged registration is not attempted in fake/screenshot runs (it writes to
    /// the registry), there the tray balloon is used.</summary>
    public void Notify(string title, string text)
    {
        var fake = App.Current?.Options.Fake == true || App.Current?.Options.ScreenshotDir != null;
        if (!fake && !_toastsFailed)
        {
            try
            {
                if (!_toastsRegistered)
                {
                    AppNotificationManager.Default.Register();
                    _toastsRegistered = true;
                }
                var toast = new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification();
                AppNotificationManager.Default.Show(toast);
                return;
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                _toastsFailed = true;
                App.Log("toast: " + e.Message);
            }
        }
        _window?.ShowBalloon(title, text);
    }

    public async Task<bool> ConfirmAsync(string title, string text, string primary)
    {
        if (_window?.Content?.XamlRoot is not { } root)
            return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = L.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            RequestedTheme = _window.Shell.ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void ApplyTheme(string theme) => _window?.ApplyTheme(theme);

    public void ApplyLanguage(string language) => _window?.DispatcherQueue.TryEnqueue(() => _window.ApplyLanguage(language));
}
