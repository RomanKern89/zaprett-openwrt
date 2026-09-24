using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Controls;

/// <summary>Small x:Bind functions (instead of IValueConverter classes).</summary>
public static class Conv
{
    public static bool Not(bool value) => !value;

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Visibility ShowBoth(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>IsEnabled of a control that changes the service: CanModify and its own condition.</summary>
    public static bool And(bool a, bool b) => a && b;

    public static bool AndNot(bool a, bool b) => a && !b;

    public static InfoBarSeverity Severity(string kind) => kind switch
    {
        MessageKind.Error or "fail" => InfoBarSeverity.Error,
        MessageKind.Warning or "warn" => InfoBarSeverity.Warning,
        MessageKind.Success or "ok" => InfoBarSeverity.Success,
        _ => InfoBarSeverity.Informational,
    };

    public static double Opacity(bool enabled) => enabled ? 1.0 : 0.55;

    /// <summary>Translucent surface of a state kind (StateColor "…-tint").</summary>
    public static string Tint(string kind) => kind + "-tint";
}
