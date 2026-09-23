using System.Text.Json.Nodes;
using Zaprett.Ipc;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>Findings of the live wizard run on Windows 10 (Windows 10 test machine, 2026-09-23).</summary>
public sealed class WizardFlowTests
{
    /// <summary>The fake service, except that chosen methods answer with an error.</summary>
    private sealed class FailingClient(FakeZaprettClient inner, string method, string code, string message) : IZaprettClient
    {
        public async Task<JsonObject> CallAsync(string m, JsonObject? args = null, CancellationToken ct = default) =>
            m == method ? new JsonObject { ["ok"] = false, ["error"] = code, ["message"] = message } : await inner.CallAsync(m, args, ct);

        public IAsyncEnumerable<ServiceEvent> SubscribeAsync(CancellationToken ct = default) => inner.SubscribeAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static async Task<WizardViewModel> AtServices(IZaprettClient client)
    {
        L.SetLanguage("ru");
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        await state.RefreshAsync();
        var vm = new WizardViewModel(state, null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Services, vm.Step);
        return vm;
    }

    [Fact]
    public async Task Every_step_asks_for_the_focus_on_its_main_button_once_it_is_enabled()
    {
        var fake = new FakeZaprettClient(FakeZaprettClient.Scenarios.FirstRun) { JobSeconds = 0.01 };
        var vm = await AtServices(fake);
        var requests = new List<(WizardStep Step, bool Forced, bool Ready)>();
        vm.PrimaryFocusRequested += (_, forced) => requests.Add((vm.Step, forced, vm.CanNext || vm.IsDone));

        // Services → Conflicts: the button is busy while the conflicts load, the focus comes after that
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Conflicts, vm.Step);
        Assert.True(vm.CanNext);
        Assert.Contains(requests, r => r.Step == WizardStep.Conflicts && r.Forced);
        // the loading disabled the button (it lost the focus); once enabled again, the page is asked once more
        Assert.True(requests.Count(r => r.Step == WizardStep.Conflicts) >= 2, string.Join(", ", requests));
        Assert.Equal(WizardStep.Conflicts, requests[^1].Step);
        Assert.True(requests[^1].Ready);

        // Conflicts → Apply: "Next" is disabled until the check has finished, then it gets the focus
        requests.Clear();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Apply, vm.Step);
        Assert.True(vm.ApplyFinished);
        var apply = Assert.Single(requests, r => r.Step == WizardStep.Apply);
        Assert.True(apply.Forced);
        Assert.True(apply.Ready);

        // Apply → Done: "Finish"
        requests.Clear();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Done, vm.Step);
        Assert.Contains(requests, r => r.Step == WizardStep.Done && r.Forced);
        Assert.All(requests, r => Assert.True(r.Ready));
    }

    [Fact]
    public void Warnings_of_the_apply_step_come_with_their_explanation()
    {
        L.SetLanguage("ru");
        var notes = WizardViewModel.Notes(Make.Json("""{"ok":true,"warnings":["config_was_invalid"]}"""), null);
        var w = UiText.Warning("config_was_invalid");
        Assert.Contains(w.Title, notes, StringComparison.Ordinal);
        Assert.Contains(w.Text, notes, StringComparison.Ordinal);
        Assert.DoesNotContain("и раньше были неверны", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Subscription_notes_show_titles_not_names()
    {
        L.SetLanguage("ru");
        var reply = Make.Json("""{"ok":true,"sources":["refilter_domains"],"sources_disabled":["antifilter_allyouneed","unknown_src"]}""");
        var list = Make.Json("""
            {"ok":true,"sources":[
              {"name":"refilter_domains","title":"Re:filter — домены"},
              {"name":"antifilter_allyouneed","title":"antifilter — всё","title_zh":"antifilter — 全部"}]}
            """);
        var notes = WizardViewModel.Notes(reply, null, list);
        Assert.Contains("Re:filter — домены", notes, StringComparison.Ordinal);
        Assert.Contains("antifilter — всё", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("refilter_domains", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("antifilter_allyouneed", notes, StringComparison.Ordinal);
        // a subscription without a title keeps its name
        Assert.Contains("unknown_src", notes, StringComparison.Ordinal);
        L.SetLanguage("zh-CN");
        try
        {
            Assert.Contains("antifilter — 全部", WizardViewModel.Notes(reply, null, list), StringComparison.Ordinal);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Fact]
    public async Task Wizard_apply_step_names_the_switched_off_subscription_by_its_title()
    {
        // "degraded": the Re:filter subscription is on and no chosen service needs it
        var fake = new FakeZaprettClient(FakeZaprettClient.Scenarios.Degraded) { JobSeconds = 0.01 };
        var vm = await AtServices(fake);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Apply, vm.Step);
        Assert.Contains("Re:filter — домены", vm.ApplyNotes, StringComparison.Ordinal);
        Assert.DoesNotContain("refilter_domains", vm.ApplyNotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_denied_on_apply_advises_a_new_sign_in_without_strategy_selection()
    {
        var fake = new FakeZaprettClient(FakeZaprettClient.Scenarios.FirstRun) { JobSeconds = 0.01 };
        var vm = await AtServices(new FailingClient(fake, "wizard.apply", "access_denied", "denied"));
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Apply, vm.Step);
        Assert.True(vm.ShowFixHint);
        Assert.Equal("access_denied", vm.ApplyErrorCode);
        Assert.Equal(L.T("Wizard.FixHint.AccessDenied"), vm.FixHintText);
        Assert.NotEqual(L.T("Wizard.FixHint"), vm.FixHintText);
        Assert.False(vm.CanGoFix);
        Assert.Equal(L.T("Err.access_denied"), vm.FailedText);
    }

    [Fact]
    public async Task Sites_that_do_not_open_still_lead_to_strategy_selection()
    {
        var fake = new FakeZaprettClient(FakeZaprettClient.Scenarios.Degraded) { JobSeconds = 0.01 };
        var vm = await AtServices(fake);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.ShowFixHint);
        Assert.Null(vm.ApplyErrorCode);
        Assert.Equal(L.T("Wizard.FixHint"), vm.FixHintText);
        Assert.True(vm.CanGoFix);
    }
}
