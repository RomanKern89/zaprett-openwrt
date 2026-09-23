using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Views;

public sealed partial class StrategiesPage : Page, IViewModelPage
{
    public StrategiesPage()
    {
        Vm = App.Get<StrategiesViewModel>();
        InitializeComponent();
    }

    public StrategiesViewModel Vm { get; }

    public PageViewModel ViewModel => Vm;

    public Task Loading { get; private set; } = Task.CompletedTask;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Loading = Vm.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Vm.Dispose();
    }

    private void OnApplyRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TestRow row)
            Vm.ApplyResultCommand.Execute(row);
    }
}
