using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Zaprett.Ui.Controls;

/// <summary>
/// Attached properties that colour an element by a state kind (ok, warn, fail, info, none, accent, onaccent,
/// Core.Text.Kind) for the element's own theme. Brushes are recomputed on ActualThemeChanged, so a page switched to
/// dark by RequestedTheme gets dark-theme colours even when the application theme is light. In high contrast the
/// element keeps the system colours. Text colours meet 4.5:1 on the Fluent card and base backgrounds of their theme,
/// also on their own tint (computed 2026-09-23 for #FBFBFB/#F3F3F3 and #202020/#2B2B2B/#323232; the accent comes
/// from the system and is checked only for the default blue).
/// </summary>
public static class StateColor
{
    public static readonly DependencyProperty ForegroundProperty = Register("Foreground");
    public static readonly DependencyProperty BackgroundProperty = Register("Background");
    public static readonly DependencyProperty FillProperty = Register("Fill");
    public static readonly DependencyProperty BorderProperty = Register("Border");

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(StateColor), new PropertyMetadata(false));

    private static readonly AccessibilitySettings Accessibility = new();

    public static string GetForeground(DependencyObject d) => (string)d.GetValue(ForegroundProperty);
    public static void SetForeground(DependencyObject d, string value) => d.SetValue(ForegroundProperty, value);
    public static string GetBackground(DependencyObject d) => (string)d.GetValue(BackgroundProperty);
    public static void SetBackground(DependencyObject d, string value) => d.SetValue(BackgroundProperty, value);
    public static string GetFill(DependencyObject d) => (string)d.GetValue(FillProperty);
    public static void SetFill(DependencyObject d, string value) => d.SetValue(FillProperty, value);
    public static string GetBorder(DependencyObject d) => (string)d.GetValue(BorderProperty);
    public static void SetBorder(DependencyObject d, string value) => d.SetValue(BorderProperty, value);

    private static DependencyProperty Register(string name) =>
        DependencyProperty.RegisterAttached(name, typeof(string), typeof(StateColor), new PropertyMetadata(null, OnChanged));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;
        if (!(bool)fe.GetValue(HookedProperty))
        {
            fe.SetValue(HookedProperty, true);
            fe.ActualThemeChanged += (s, _) => Apply((FrameworkElement)s);
        }
        Apply(fe);
    }

    private static void Apply(FrameworkElement fe)
    {
        if (Accessibility.HighContrast)
            return;
        var dark = fe.ActualTheme == ElementTheme.Dark;
        if (fe.GetValue(ForegroundProperty) is string fg && Brush(fg, dark) is { } fb)
        {
            switch (fe)
            {
                case TextBlock t: t.Foreground = fb; break;
                case IconElement i: i.Foreground = fb; break;
                case Control c: c.Foreground = fb; break;
                case RichTextBlock r: r.Foreground = fb; break;
            }
        }
        if (fe.GetValue(BackgroundProperty) is string bg && Brush(bg, dark) is { } bb)
        {
            switch (fe)
            {
                case Border b: b.Background = bb; break;
                case Panel p: p.Background = bb; break;
                case Control c: c.Background = bb; break;
            }
        }
        if (fe.GetValue(FillProperty) is string fill && Brush(fill, dark) is { } fl && fe is Shape shape)
            shape.Fill = fl;
        if (fe.GetValue(BorderProperty) is string border && Brush(border, dark) is { } br)
        {
            switch (fe)
            {
                case Border b: b.BorderBrush = br; break;
                case Control c: c.BorderBrush = br; break;
            }
        }
    }

    public static SolidColorBrush? Brush(string kind, bool dark) => ColorOf(kind, dark) is { } c ? new SolidColorBrush(c) : null;

    /// <summary>Colours of the kinds: Fluent system status colours (light/dark), accent from the system.</summary>
    public static Color? ColorOf(string kind, bool dark)
    {
        // "ok-tint" etc.: the same hue as a translucent surface (no Opacity layer needed)
        if (kind.EndsWith("-tint", StringComparison.Ordinal) && BaseColor(kind[..^5], dark) is { } c)
            return Color.FromArgb(dark ? (byte)0x26 : (byte)0x18, c.R, c.G, c.B);
        return BaseColor(kind, dark);
    }

    private static Color? BaseColor(string kind, bool dark) => kind switch
    {
        "ok" => dark ? Hex(0x6C, 0xCB, 0x5F) : Hex(0x0E, 0x70, 0x0E),
        "warn" => dark ? Hex(0xFC, 0xE1, 0x00) : Hex(0x8F, 0x55, 0x00),
        "fail" => dark ? Hex(0xFF, 0x99, 0xA4) : Hex(0xB8, 0x28, 0x1A),
        "info" or "accent" => Accent(dark),
        "none" => dark ? Hex(0xB4, 0xB4, 0xB4) : Hex(0x5C, 0x5C, 0x5C),
        "onaccent" => dark ? Colors.Black : Colors.White,
        _ => null,
    };

    private static Color Accent(bool dark)
    {
        var key = dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1";
        return Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : dark ? Hex(0x99, 0xEB, 0xFF) : Hex(0x00, 0x5F, 0xB8);
    }

    private static Color Hex(byte r, byte g, byte b, byte a = 0xFF) => Color.FromArgb(a, r, g, b);
}
