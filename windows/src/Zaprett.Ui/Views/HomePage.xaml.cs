using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Views;

public sealed partial class HomePage : Page, IViewModelPage
{
    public HomePage()
    {
        Vm = App.Get<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel Vm { get; }

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
