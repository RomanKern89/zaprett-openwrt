using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;
using Zaprett.Ui.Views;

namespace Zaprett.Ui.Platform;

/// <summary>
/// "--fake --screenshot &lt;dir&gt; [--theme light|dark] [--lang ru|en]": walks through every screen and state of the
/// fake service and saves the window to PNG (PrintWindow, RenderTargetBitmap as the fallback; the backdrop is replaced
/// by the solid base colour so the pictures do not depend on the desktop behind). Long pages are captured in several
/// scrolled parts.
/// </summary>
public sealed partial class Screenshotter(MainWindow window, AppState state, string dir)
{
    private readonly string _prefix = (window.Shell.ActualTheme == ElementTheme.Dark ? "dark" : "light") + "-" + L.Language;
    private int _n;

    public List<string> Files { get; } = [];

    private FakeZaprettClient Fake => state.Fake ?? throw new InvalidOperationException("screenshots need --fake");

    public async Task RunAsync()
    {
        Directory.CreateDirectory(dir);
        window.UseSolidBackground();
        Fake.JobSeconds = 0.15;
        await Settle(800);
        await RunStepsAsync();
        await File.WriteAllTextAsync(Path.Combine(dir, $"{_prefix}-files.txt"), string.Join('\n', Files) + '\n');
    }

    private async Task Scenario(string name)
    {
        App.Log("screenshot: scenario " + name);
        Fake.SetScenario(name);
        await state.RefreshAsync();
        await Settle(300);
    }

    private async Task<T> Open<T>(string page) where T : PageViewModel
    {
        window.Shell.Navigate(page);
        await Settle(250);
        if (window.Shell.CurrentContent is IViewModelPage p)
        {
            await p.Loading;
            await Settle(400);
            return (T)p.ViewModel;
        }
        throw new InvalidOperationException("page " + page + " has no view model");
    }

    private static async Task Settle(int ms) => await Task.Delay(ms);

    /// <summary>Renders the window content; with scrollParts, also the rest of the first scroll viewer of the page.</summary>
    private async Task Shot(string name, bool scrollParts = true)
    {
        App.Log("screenshot: " + name);
        var root = window.Shell.RootElement;
        // the same picture in every set: whether the focused control draws its focus ring depends on the input of
        // the run (a programmatic focus inherits the keyboard look, checked on 2026-09-23), so the ring is switched
        // off for the shot and back on after it
        var focused = root.XamlRoot == null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) as Control;
        var ring = focused?.UseSystemFocusVisuals ?? false;
        if (focused != null)
            focused.UseSystemFocusVisuals = false;
        try
        {
            await ShotParts(root, name, scrollParts);
        }
        finally
        {
            if (focused != null)
                focused.UseSystemFocusVisuals = ring;
        }
    }

    private async Task ShotParts(FrameworkElement root, string name, bool scrollParts)
    {
        await Settle(350);
        await Save(root, name);
        if (!scrollParts)
            return;
        var page = window.Shell.CurrentContent;
        var scroller = page == null ? null : FindScroller(page);
        if (scroller == null || scroller.ScrollableHeight < 40)
            return;
        var part = 2;
        var offset = 0.0;
        while (offset < scroller.ScrollableHeight && part <= 4)
        {
            offset = Math.Min(scroller.ScrollableHeight, offset + scroller.ViewportHeight * 0.85);
            scroller.ChangeView(null, offset, null, disableAnimation: true);
            await Settle(400);
            await Save(root, $"{name}-part{part++}");
        }
        scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static ScrollViewer? FindScroller(DependencyObject d)
    {
        if (d is ScrollViewer sv)
            return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var found = FindScroller(VisualTreeHelper.GetChild(d, i));
            if (found != null)
                return found;
        }
        return null;
    }

    private async Task Save(UIElement element, string name)
    {
        byte[] pixels;
        int width, height;
        var captured = WindowCapture.Capture(WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (captured is { } c)
            (pixels, width, height) = c;
        else
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element);
            pixels = (await rtb.GetPixelsAsync()).ToArray();
            (width, height) = (rtb.PixelWidth, rtb.PixelHeight);
        }
        var file = Path.Combine(dir, $"{_prefix}-{++_n:00}-{name}.png");
        using (var stream = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var scale = element.XamlRoot?.RasterizationScale ?? 1.0;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height,
                96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(file, bytes);
        }
        Files.Add(Path.GetFileName(file));
    }
}
