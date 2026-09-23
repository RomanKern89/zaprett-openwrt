using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Views;

public sealed partial class WizardPage : Page, IViewModelPage
{
    public WizardPage()
    {
        Vm = App.Get<WizardViewModel>();
        InitializeComponent();
        // Enter must go forward on every step, never skip the wizard (the link "Skip" is first in tab order): the view
        // model asks for the focus after each step change and when the main button is enabled again after a busy state.
        Vm.PrimaryFocusRequested += OnPrimaryFocusRequested;
        Loaded += (_, _) => Vm.RequestFocusOnStart();
        // the button is also disabled while its command still runs (after "Apply and start" the state is refreshed
        // once more): a pending focus is completed as soon as it is enabled, whatever disabled it
        NextButton.IsEnabledChanged += (_, e) =>
        {
            if (e.NewValue is true && _forcedPending)
                OnPrimaryFocusRequested(this, true);
        };
    }

    /// <summary>
    /// A step change whose main button was still disabled when the focus was due (checked on 2026-09-23: on
    /// "Conflicts" the loading disables it first and WinUI gives the focus to "Check again"): the next request, when
    /// the button is enabled, is then forced too.
    /// </summary>
    private bool _forcedPending;

    public WizardViewModel Vm { get; }

    public PageViewModel ViewModel => Vm;

    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Focus is moved after the bindings of the new step have been applied (visibility, enabled state).</summary>
    private void OnPrimaryFocusRequested(object? sender, bool forced) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusPrimary(forced));

    private void FocusPrimary(bool forced)
    {
        if (XamlRoot == null)
            return;
        var target = Vm.IsDone ? FinishButton : NextButton;
        forced |= _forcedPending;
        if (!target.IsEnabled || target.Visibility != Visibility.Visible)
        {
            _forcedPending |= forced;
            return;
        }
        _forcedPending = false;
        if (!forced && FocusManager.GetFocusedElement(XamlRoot) is Control { IsEnabled: true } current && current != target)
            return;
        if (!target.Focus(FocusState.Programmatic))
            App.Log($"wizard: focus on the main button of step {Vm.Step} failed");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Loading = Vm.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Vm.PrimaryFocusRequested -= OnPrimaryFocusRequested;
        Vm.Dispose();
    }
}
