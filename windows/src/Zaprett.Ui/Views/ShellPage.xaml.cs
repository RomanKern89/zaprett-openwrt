using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Views;

/// <summary>Pages that show a view model (for the screenshot run and loading).</summary>
public interface IViewModelPage
{
    PageViewModel ViewModel { get; }

    Task Loading { get; }
}

/// <summary>Window content: title bar, navigation, pages, the first-run wizard and the "service down" screen.</summary>
public sealed partial class ShellPage : Page
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["home"] = typeof(HomePage),
        ["services"] = typeof(ServicesPage),
        ["strategies"] = typeof(StrategiesPage),
        ["lists"] = typeof(ListsPage),
        ["diagnostics"] = typeof(DiagnosticsPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly MainWindow _window;
    private readonly AppState _state;
    private bool _syncingSelection;

    public ShellPage(MainWindow window, AppState state, ShellViewModel vm, Navigator navigator)
    {
        _window = window;
        _state = state;
        Vm = vm;
        InitializeComponent();
        navigator.Target = Navigate;
    }

    public ShellViewModel Vm { get; }

    public UIElement TitleBarElement => AppTitleBar;

    public Grid RootElement => RootGrid;

    public string CurrentPage { get; private set; } = "";

    public Page? CurrentContent => WizardFrame.Visibility == Visibility.Visible ? WizardFrame.Content as Page : ContentFrame.Content as Page;

    public void Start(string? page)
    {
        // screenshots open every screen themselves
        var decision = _window.IsScreenshotRun ? false : WizardDecision.ShouldOpen(_state.Prefs.WizardDone, _state.Status);
        if (decision == true)
        {
            ShowWizard();
            return;
        }
        Navigate(page ?? "home");
        if (decision == false)
            MarkWizardDone();
        else
            _state.PropertyChanged += DecideWizardWhenStatusArrives;
    }

    /// <summary>The first status of the service decides: a service set up by the installer or the command line gets
    /// no wizard; a service nobody set up does, unless the person has already gone to another page.</summary>
    private void DecideWizardWhenStatusArrives(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.Status) || _state.Status == null)
            return;
        _state.PropertyChanged -= DecideWizardWhenStatusArrives;
        var decision = WizardDecision.ShouldOpen(_state.Prefs.WizardDone, _state.Status);
        if (decision == true && CurrentPage == "home")
            ShowWizard();
        else if (decision == false)
            MarkWizardDone();
    }

    private void MarkWizardDone()
    {
        if (_state.Prefs.WizardDone)
            return;
        _state.Prefs.WizardDone = true;
        _state.Prefs.Save();
    }

    /// <summary>Opens a page by tag; "wizard" opens the wizard, "dns" the settings.</summary>
    public void Navigate(string page)
    {
        if (page == "wizard")
        {
            ShowWizard();
            return;
        }
        if (page == "dns")
            page = "settings";
        if (!Pages.TryGetValue(page, out var type))
            return;
        HideWizard();
        CurrentPage = page;
        if (ContentFrame.Content?.GetType() != type)
            ContentFrame.Navigate(type, null, new EntranceNavigationTransitionInfo());
        _syncingSelection = true;
        Nav.SelectedItem = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == page);
        _syncingSelection = false;
    }

    public void ShowWizard()
    {
        CurrentPage = "wizard";
        WizardFrame.Visibility = Visibility.Visible;
        Nav.Visibility = Visibility.Collapsed;
        WizardFrame.Navigate(typeof(WizardPage), null, new DrillInNavigationTransitionInfo());
        if (WizardFrame.Content is WizardPage wp)
            wp.Vm.Finished += (_, _) => Navigate("home");
    }

    private void HideWizard()
    {
        if (WizardFrame.Visibility == Visibility.Collapsed)
            return;
        WizardFrame.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;
        (WizardFrame.Content as IViewModelPage)?.ViewModel.Dispose();
        WizardFrame.Content = null;
    }

    /// <summary>Called before the shell is replaced (language change): the pages get no OnNavigatedFrom then, so
    /// their view models are released here and stop following the shared state.</summary>
    public void Release()
    {
        _state.PropertyChanged -= DecideWizardWhenStatusArrives;
        (ContentFrame.Content as IViewModelPage)?.ViewModel.Dispose();
        (WizardFrame.Content as IViewModelPage)?.ViewModel.Dispose();
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: "wizard" })
            ShowWizard();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
            return;
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            Navigate(tag);
    }
}
