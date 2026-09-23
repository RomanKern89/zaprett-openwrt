using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Views;

public sealed partial class ListsPage : Page, IViewModelPage
{
    public ListsPage()
    {
        Vm = App.Get<ListsViewModel>();
        InitializeComponent();
    }

    public ListsViewModel Vm { get; }

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

    private void OnWhitelist(object sender, RoutedEventArgs e) => Vm.IsBlacklist = false;

    private void OnBlacklist(object sender, RoutedEventArgs e) => Vm.IsBlacklist = true;

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ShowTab((sender.SelectedItem?.Tag as string) ?? "domains");

    /// <summary>Shows one of the tabs: domains, networks, exclusions, own, sources.</summary>
    public void SelectTab(string tag)
    {
        foreach (var item in Tabs.Items)
            item.IsSelected = (string)item.Tag == tag;
        ShowTab(tag);
    }

    private void ShowTab(string tag)
    {
        DomainsPanel.Visibility = tag == "domains" ? Visibility.Visible : Visibility.Collapsed;
        NetworksPanel.Visibility = tag == "networks" ? Visibility.Visible : Visibility.Collapsed;
        ExclusionsPanel.Visibility = tag == "exclusions" ? Visibility.Visible : Visibility.Collapsed;
        OwnPanel.Visibility = tag == "own" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPanel.Visibility = tag == "sources" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDeleteSource(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SourceItem item)
            Vm.DeleteSourceCommand.Execute(item);
    }
}
