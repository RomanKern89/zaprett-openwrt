using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Views;

public sealed partial class SettingsPage : Page, IViewModelPage
{
    public SettingsPage()
    {
        Vm = App.Get<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel Vm { get; }

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
}
