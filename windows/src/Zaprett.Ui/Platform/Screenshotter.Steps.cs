using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;
using Zaprett.Ui.Views;
using S = Zaprett.Ui.Core.DevFakes.FakeZaprettClient.Scenarios;

namespace Zaprett.Ui.Platform;

public sealed partial class Screenshotter
{
    /// <summary>Screens and states that are captured (plan §4.2), in order. Commands that would open a confirmation
    /// dialog are replaced by the same service calls, so the run never waits for a person.</summary>
    private async Task RunStepsAsync()
    {
        // working bypass: every page
        await Scenario(S.Running);
        await Open<HomeViewModel>("home");
        await Shot("home");

        await Open<ServicesViewModel>("services");
        await Shot("services");

        var strategies = await Open<StrategiesViewModel>("strategies");
        await state.RunJobAsync("test.start", new JsonObject { ["quick"] = true }, null);
        await strategies.LoadAsync();
        strategies.Selected = strategies.Strategies.FirstOrDefault(s => s.IsUser);
        await Settle(600);
        await strategies.SaveAndCheckCommand.ExecuteAsync(null);
        await Shot("strategies");

        var lists = await Open<ListsViewModel>("lists");
        await Shot("lists-domains", scrollParts: false);
        (window.Shell.CurrentContent as ListsPage)?.SelectTab("own");
        await Shot("lists-own", scrollParts: false);
        (window.Shell.CurrentContent as ListsPage)?.SelectTab("sources");
        await Shot("lists-subscriptions", scrollParts: false);
        _ = lists;

        var diag = await Open<DiagnosticsViewModel>("diagnostics");
        await diag.DiagnoseCommand.ExecuteAsync(null);
        await diag.CheckConfigCommand.ExecuteAsync(null);
        await Shot("diagnostics");

        await Open<SettingsViewModel>("settings");
        await Shot("settings");

        // other states of the home page
        foreach (var scenario in new[] { S.Degraded, S.Testing, S.Error, S.Stopped })
        {
            await Scenario(scenario);
            window.Shell.Navigate("services");
            await Open<HomeViewModel>("home");
            await Shot("home-" + scenario, scrollParts: false);
        }

        await Scenario(S.Testing);
        await Open<StrategiesViewModel>("strategies");
        await Shot("strategies-testing", scrollParts: false);

        // the service is not running
        Fake.SetScenario(S.Unavailable);
        try
        {
            await state.RefreshAsync();
        }
        catch (ZaprettCallException)
        {
            // expected: the shell shows its "service down" screen
        }
        await Shot("service-down", scrollParts: false);

        await WizardAsync();
    }

    /// <summary>The first-run wizard from the intro to the result, with a blocking conflict and a failed check.</summary>
    private async Task WizardAsync()
    {
        Fake.SetScenario(S.FirstRun);
        await state.RefreshAsync();
        window.Shell.ShowWizard();
        await Settle(600);
        var wizard = (window.Shell.CurrentContent as WizardPage)!.Vm;
        await Shot("wizard-1-intro", scrollParts: false);

        await wizard.NextCommand.ExecuteAsync(null);
        await Shot("wizard-2-services", scrollParts: false);

        Fake.SetScenario(S.Conflicts);
        await wizard.NextCommand.ExecuteAsync(null);
        await Shot("wizard-3-conflicts", scrollParts: false);

        Fake.SetScenario(S.Degraded);
        await wizard.NextCommand.ExecuteAsync(null);
        await Shot("wizard-4-check", scrollParts: false);

        wizard.GoFixCommand.Execute(null);
        await wizard.DiagnoseCommand.ExecuteAsync(null);
        await Shot("wizard-5-fix");
        await wizard.AutoSelectCommand.ExecuteAsync(null);
        ScrollToEnd();
        await Shot("wizard-5-fixed", scrollParts: false);

        await wizard.NextCommand.ExecuteAsync(null);
        await Shot("wizard-6-done", scrollParts: false);
    }

    private void ScrollToEnd()
    {
        if (window.Shell.CurrentContent is { } page && FindScroller(page) is { } sv)
            sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
    }
}
