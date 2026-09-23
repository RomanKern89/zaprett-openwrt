using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Controls;

/// <summary>
/// Read-only strategy text with highlighted parameters: filters (--filter-*, --wf-*), options, values, placeholders
/// ${…}, profile separators (--new) and comments. Text is added as Runs, never parsed as markup. Recoloured when the
/// theme changes.
/// </summary>
public sealed partial class StrategyText : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StrategyText), new PropertyMetadata("", (d, _) => ((StrategyText)d).Render()));

    private readonly RichTextBlock _block = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        LineHeight = 20,
    };

    public StrategyText()
    {
        Content = _block;
        ActualThemeChanged += (_, _) => Render();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void Render()
    {
        _block.Blocks.Clear();
        var dark = ActualTheme == ElementTheme.Dark;
        foreach (var line in (Text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var token in StrategyTokenizer.Tokenize(line))
            {
                var run = new Run { Text = token.Text };
                var color = ColorOf(token.Kind, dark);
                if (color != null)
                    run.Foreground = new SolidColorBrush(color.Value);
                if (token.Kind == TokenKind.Separator)
                    run.FontWeight = FontWeights.Bold;
                if (token.Kind == TokenKind.Comment)
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                p.Inlines.Add(run);
            }
            _block.Blocks.Add(p);
        }
    }

    /// <summary>Token colours, each ≥ 4.5:1 on the card background of its theme.</summary>
    public static Color? ColorOf(TokenKind kind, bool dark) => kind switch
    {
        TokenKind.Filter => dark ? Color.FromArgb(255, 0xC5, 0xA6, 0xF2) : Color.FromArgb(255, 0x6B, 0x3F, 0xA0),
        TokenKind.Option => StateColor.ColorOf("accent", dark),
        TokenKind.Placeholder => dark ? Color.FromArgb(255, 0xFC, 0xE1, 0x00) : Color.FromArgb(255, 0x8A, 0x51, 0x00),
        TokenKind.Separator => StateColor.ColorOf("fail", dark),
        TokenKind.Comment => StateColor.ColorOf("none", dark),
        _ => null,
    };
}
