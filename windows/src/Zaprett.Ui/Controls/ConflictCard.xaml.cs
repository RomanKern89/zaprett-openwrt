using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Controls;

public sealed partial class ConflictCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(ConflictItem), typeof(ConflictCard), new PropertyMetadata(null));

    public ConflictCard()
    {
        InitializeComponent();
    }

    public ConflictItem Item
    {
        get => (ConflictItem)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
