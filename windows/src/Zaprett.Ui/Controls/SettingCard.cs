using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Zaprett.Ui.Controls;

/// <summary>A row of the settings page: icon, title, explanation and the control on the right (style in Theme.xaml).</summary>
public sealed partial class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata("", OnHeaderChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public SettingCard()
    {
        DefaultStyleKey = typeof(SettingCard);
        IsTabStop = false;
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The control on the right gets the title as its accessible name, unless it has its own.</summary>
    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingCard)d;
        if (card.Content is DependencyObject content && string.IsNullOrEmpty(AutomationProperties.GetName(content)))
            AutomationProperties.SetName(content, (string)e.NewValue);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is DependencyObject content && string.IsNullOrEmpty(AutomationProperties.GetName(content)))
            AutomationProperties.SetName(content, Header);
    }
}
