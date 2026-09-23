using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

public sealed class WizardTests
{
    private static async Task<(WizardViewModel Vm, FakeZaprettClient Fake)> AtServices(string scenario = FakeZaprettClient.Scenarios.FirstRun)
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State(scenario);
        await state.RefreshAsync();
        var vm = new WizardViewModel(state, null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Services, vm.Step);
        return (vm, fake);
    }

    [Fact]
    public async Task First_run_preselects_the_default_services()
    {
        var (vm, _) = await AtServices();
        var selected = vm.Services.Where(s => s.IsSelected).Select(s => s.Id).OrderBy(x => x).ToList();
        Assert.Equal(["discord", "youtube"], selected);
    }

    [Fact]
    public async Task Services_that_cannot_help_are_not_selectable_and_listed_last()
    {
        var (vm, _) = await AtServices();
        var whatsapp = vm.Services.Single(s => s.Id == "whatsapp");
        Assert.False(whatsapp.IsSelectable);
        whatsapp.IsSelected = true;
        Assert.False(whatsapp.IsSelected);
        Assert.Equal(Kind.Fail, whatsapp.WorksKind);
        Assert.True(vm.Services.ToList().IndexOf(whatsapp) > vm.Services.ToList().FindLastIndex(s => s.IsSelectable));
    }

    [Fact]
    public async Task Nothing_selected_stays_on_the_services_step()
    {
        var (vm, _) = await AtServices();
        foreach (var s in vm.Services)
            s.IsSelected = false;
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Services, vm.Step);
        Assert.True(vm.HasMessage);
        Assert.Equal(MessageKind.Warning, vm.MessageKindValue);
    }

    [Fact]
    public async Task Variant_goes_into_the_reference()
    {
        var (vm, _) = await AtServices();
        var discord = vm.Services.Single(s => s.Id == "discord");
        Assert.True(discord.HasVariants);
        discord.SelectedVariant = discord.Variants.Single(v => v.Id == "voice");
        Assert.Equal("discord:voice", discord.Reference);
        // names of the lists in the text, ids only in the tooltip
        Assert.DoesNotContain("zaprett-discord-voice", discord.Contents, StringComparison.Ordinal);
        Assert.Contains("zaprett-discord-voice", discord.ContentsIds, StringComparison.Ordinal);
        Assert.Contains("discord:voice", vm.SelectedReferences);
    }

    [Fact]
    public async Task Happy_path_goes_to_done_and_enables_the_lists()
    {
        var (vm, fake) = await AtServices();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Conflicts, vm.Step);
        Assert.True(vm.ConflictsLoaded);
        Assert.False(vm.HasBlockingConflicts);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Apply, vm.Step);
        Assert.True(vm.ApplyFinished);
        Assert.All(vm.ApplySteps, s => Assert.Equal("done", s.State));
        Assert.False(vm.ApplyFailed);
        Assert.True(fake.Running);
        Assert.Contains(fake.Calls, c => c == "wizard.apply");
        Assert.NotEmpty(vm.Results);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Done, vm.Step);
        Assert.Equal(Kind.Ok, vm.DoneKind);
        var finished = false;
        vm.Finished += (_, _) => finished = true;
        await vm.FinishCommand.ExecuteAsync(null);
        Assert.True(finished);
        Assert.True(vm.State.Prefs.WizardDone);
        Assert.Contains(fake.Calls, c => c == "enable");
    }

    [Fact]
    public async Task Failed_sites_lead_to_fix_and_selection_repairs_them()
    {
        var (vm, fake) = await AtServices(FakeZaprettClient.Scenarios.Degraded);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.ApplyFailed);
        Assert.Contains("YouTube", vm.FailedText, StringComparison.Ordinal);
        vm.GoFixCommand.Execute(null);
        Assert.Equal(WizardStep.Fix, vm.Step);

        await vm.DiagnoseCommand.ExecuteAsync(null);
        Assert.True(vm.HasDiagnose);
        Assert.NotEmpty(vm.DiagnoseRows);

        await vm.AutoSelectCommand.ExecuteAsync(null);
        Assert.True(vm.HasTestResult);
        Assert.Contains(fake.Calls, c => c == "test.start");
        Assert.False(vm.ApplyFailed);
    }

    [Fact]
    public async Task Blocking_conflict_is_flagged()
    {
        var (vm, _) = await AtServices(FakeZaprettClient.Scenarios.Conflicts);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.HasBlockingConflicts);
        Assert.Equal(Kind.Fail, vm.Conflicts[0].Kind);
        Assert.False(vm.NoConflicts);
    }

    [Fact]
    public async Task Apply_error_marks_the_running_step_failed()
    {
        L.SetLanguage("en");
        var client = new ScriptedClient((m, _) => m switch
        {
            "wizard.apply" => Make.Json("""{"ok":false,"error":"preset_unavailable","message":"нельзя"}"""),
            "presets" => Make.Json("""{"ok":true,"services":[{"id":"youtube","name":"YouTube","works":"yes","lists":["zaprett-youtube"]}],"defaults":{"services":["youtube"]}}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state = new Core.Services.AppState(client, new FakePlatform(), Core.Services.UiPrefs.Load(null));
        var vm = new WizardViewModel(state, null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Apply, vm.Step);
        Assert.Equal("failed", vm.ApplySteps[0].State);
        Assert.Equal("pending", vm.ApplySteps[1].State);
        Assert.True(vm.ApplyFailed);
        Assert.Equal(L.T("Err.preset_unavailable"), vm.ApplySteps[0].Detail);
    }

    [Fact]
    public void Notes_explain_skipped_variants_and_subscriptions()
    {
        L.SetLanguage("en");
        var reply = Make.Json("""
            {"ok":true,"skipped":[{"id":"whatsapp","reason":"works_no"}],"variants":{"discord":"voice","youtube":null},
             "sources":["cloudflare_v4"],"sources_disabled":["refilter_domains"],"job_error":{"code":"download_failed","message":"x"},"warnings":["low_memory"]}
            """);
        var notes = WizardViewModel.Notes(reply, null);
        Assert.Contains("whatsapp", notes, StringComparison.Ordinal);
        Assert.Contains("discord — voice", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("youtube —", notes, StringComparison.Ordinal);
        Assert.Contains("cloudflare_v4", notes, StringComparison.Ordinal);
        Assert.Contains("refilter_domains", notes, StringComparison.Ordinal);
        Assert.Contains(L.T("Err.download_failed"), notes, StringComparison.Ordinal);
        Assert.Contains(L.T("Warn.low_memory.Title"), notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Rail_follows_the_step()
    {
        L.SetLanguage("en");
        var (state, _, _) = Make.State();
        var vm = new WizardViewModel(state, null);
        Assert.True(vm.Rail[0].IsCurrent);
        vm.Step = WizardStep.Apply;
        Assert.True(vm.Rail[3].IsCurrent);
        Assert.True(vm.Rail[2].IsDone);
        Assert.False(vm.Rail[4].IsDone);
        Assert.False(vm.CanNext);
    }
}
