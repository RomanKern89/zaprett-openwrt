using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Zaprett.Ui.Core.Services;

namespace Zaprett.Ui.Platform;

/// <summary>Work areas and DPI of the monitors for <see cref="WindowPlacement"/>.</summary>
public static class Monitors
{
    private const uint MonitorDefaultToPrimary = 1;
    private const int MdtEffectiveDpi = 0;

    public static PixelRect ToPixelRect(Windows.Graphics.RectInt32 r) => new(r.X, r.Y, r.Width, r.Height);

    public static PixelRect PrimaryWorkArea() => ToPixelRect(DisplayArea.Primary.WorkArea);

    public static List<PixelRect> AllWorkAreas()
    {
        var result = new List<PixelRect>();
        var all = DisplayArea.FindAll();
        // index access: enumerating this collection throws in some Windows App SDK versions
        for (var i = 0; i < all.Count; i++)
            result.Add(ToPixelRect(all[i].WorkArea));
        return result;
    }

    /// <summary>DPI scale of the monitor that holds the point (1.0 = 96 DPI); 1.0 when it cannot be read.</summary>
    public static double ScaleAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToPrimary);
        return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
