using System.Runtime.InteropServices;

namespace Zaprett.Ui.Platform;

/// <summary>
/// Captures the window as the person sees it (PrintWindow with PW_RENDERFULLCONTENT), including the title bar and
/// the caption buttons. RenderTargetBitmap draws some composition effects with artefacts, so it is only the fallback.
/// </summary>
internal static partial class WindowCapture
{
    private const uint PwRenderFullContent = 2;

    /// <summary>BGRA pixels (top-down) of the window, or null when the capture failed.</summary>
    public static (byte[] Pixels, int Width, int Height)? Capture(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r))
            return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0)
            return null;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(hwnd, dc, PwRenderFullContent))
                return null;
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32, Compression = 0,
            };
            var pixels = new byte[w * h * 4];
            SelectObject(dc, old);
            if (GetDIBits(dc, bmp, 0, (uint)h, pixels, ref info, 0) != h)
                return null;
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255; // GDI leaves alpha undefined
            return (pixels, w, h);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
}
