namespace Zaprett.Ui.Core.Services;

/// <summary>A rectangle in physical pixels (the units of AppWindow and DisplayArea).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;

    public int CenterY => Y + Height / 2;

    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Where the main window opens. The preferred size (in DIP, scaled by the monitor DPI) never exceeds the work area of
/// the monitor: on a 1280×800 screen at 100 % the whole window with its caption buttons stays visible. A saved
/// position is reused only while its centre is on one of the current monitors, and is pulled inside that work area.
/// </summary>
public static class WindowPlacement
{
    public const int PreferredWidth = 1280;
    public const int PreferredHeight = 860;
    // the pages are laid out down to this size (checked on the screenshots); the start size is the preferred one,
    // so it is below 1000×680 only on a screen smaller than that
    public const int DragMinimumWidth = 760;
    public const int DragMinimumHeight = 560;

    /// <param name="workArea">Work area of the monitor the window starts on (without the taskbar).</param>
    /// <param name="scale">DPI scale of that monitor (1.0 = 96 DPI).</param>
    /// <param name="saved">Saved position and size of the last session, or null.</param>
    /// <param name="workAreas">Work areas of all current monitors (used to check the saved position).</param>
    public static PixelRect Compute(PixelRect workArea, double scale, PixelRect? saved, IReadOnlyList<PixelRect> workAreas)
    {
        if (scale is not > 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            scale = 1.0;
        if (saved is { Width: > 0, Height: > 0 } s)
        {
            foreach (var area in workAreas)
                if (area.Contains(s.CenterX, s.CenterY))
                    return Clamp(s, area);
        }
        var width = Math.Min((int)Math.Round(PreferredWidth * scale), workArea.Width);
        var height = Math.Min((int)Math.Round(PreferredHeight * scale), workArea.Height);
        return new PixelRect(workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2, width, height);
    }

    /// <summary>Smallest size the person can drag the window to: the layout minimum, or the work area if smaller.</summary>
    public static (int Width, int Height) MinimumSize(PixelRect workArea, double scale)
    {
        if (scale is not > 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            scale = 1.0;
        return (Math.Min((int)Math.Round(DragMinimumWidth * scale), workArea.Width), Math.Min((int)Math.Round(DragMinimumHeight * scale), workArea.Height));
    }

    private static PixelRect Clamp(PixelRect r, PixelRect area)
    {
        var width = Math.Min(r.Width, area.Width);
        var height = Math.Min(r.Height, area.Height);
        var x = Math.Clamp(r.X, area.X, area.X + area.Width - width);
        var y = Math.Clamp(r.Y, area.Y, area.Y + area.Height - height);
        return new PixelRect(x, y, width, height);
    }
}
