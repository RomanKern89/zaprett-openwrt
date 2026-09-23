using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Controls;

public sealed partial class ServiceCard : UserControl
{
    public static readonly DependencyProperty ChoiceProperty =
        DependencyProperty.Register(nameof(Choice), typeof(ServiceChoice), typeof(ServiceCard), new PropertyMetadata(null));

    public ServiceCard()
    {
        InitializeComponent();
    }

    public ServiceChoice Choice
    {
        get => (ServiceChoice)GetValue(ChoiceProperty);
        set => SetValue(ChoiceProperty, value);
    }
}
