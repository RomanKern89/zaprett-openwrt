using System.Text.Json.Nodes;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

public sealed class HomeViewModelTests
{
    [Theory]
    [InlineData(FakeZaprettClient.Scenarios.Running, Kind.Ok, "Home.Hero.On")]
    [InlineData(FakeZaprettClient.Scenarios.Stopped, Kind.None, "Home.Hero.Off")]
    [InlineData(FakeZaprettClient.Scenarios.Error, Kind.Fail, "Home.Hero.Broken")]
    [InlineData(FakeZaprettClient.Scenarios.Degraded, Kind.Warn, "Home.Hero.Degraded")]
    [InlineData(FakeZaprettClient.Scenarios.Testing, Kind.Info, "Home.Hero.Testing")]
    public async Task Hero_answers_does_it_work(string scenario, string kind, string titleKey)
    {
        L.SetLanguage("en");
        var (state, _, _) = Make.State(scenario);
        await state.RefreshAsync();
        var vm = new HomeViewModel(state, null);
        Assert.Equal(kind, vm.HeroKind);
        Assert.Equal(L.T(titleKey), vm.HeroTitle);
    }

    [Fact]
    public void Hero_without_status_is_loading()
    {
        L.SetLanguage("en");
        Assert.Equal(L.T("Home.Hero.Loading"), HomeViewModel.Hero(null, false, false, null, false).Title);
    }

    [Fact]
    public async Task Tiles_history_and_warnings_come_from_the_state()
    {
        L.SetLanguage("en");
        var (state, _, _) = Make.State(FakeZaprettClient.Scenarios.Degraded);
        await state.RefreshAsync();
        var vm = new HomeViewModel(state, null);
        var youtube = vm.Tiles.Single(t => t.Id == "youtube");
        Assert.Equal(Kind.Fail, youtube.Kind);
        Assert.Equal("0/3", youtube.Result);
        Assert.Equal(L.T("TargetErr.timeout"), youtube.Detail);
        Assert.Equal(HomeViewModel.HistoryLength, vm.History.Count);
        Assert.Equal(Kind.Fail, vm.History[^1].Kind);
        Assert.Contains(vm.Warnings, w => w.Code == "monitor_degraded");
        Assert.Equal(Kind.Fail, vm.MonitorKind);
        Assert.True(vm.HasPackets);
        Assert.Equal("184,233", vm.PacketsText);
    }

    [Fact]
    public void History_keeps_only_the_last_48_and_marks_empty_checks()
    {
        var items = Enumerable.Range(0, 60).Select(i => Make.Json($$"""{"t":{{1_800_000_000 + i}},"ok":{{(i == 59 ? 0 : 5)}},"total":{{(i == 58 ? 0 : 5)}}}""")).ToList();
        var cells = HomeViewModel.BuildHistory(items);
        Assert.Equal(48, cells.Count);
        Assert.Equal(Kind.None, cells[^2].Kind);
        Assert.Equal(Kind.Fail, cells[^1].Kind);
        Assert.Equal(Kind.Ok, cells[0].Kind);
    }

    [Fact]
    public async Task Toggle_stops_and_starts_the_bypass()
    {
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new HomeViewModel(state, null);
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.False(fake.Running);
        Assert.False(vm.IsOn);
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.True(fake.Running);
        Assert.True(vm.IsOn);
    }

    [Fact]
    public async Task Toggle_failure_shows_a_human_error()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State(FakeZaprettClient.Scenarios.Error);
        await state.RefreshAsync();
        var vm = new HomeViewModel(state, null);
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.True(vm.HasMessage);
        Assert.Equal(MessageKind.Error, vm.MessageKindValue);
        Assert.Equal(L.T("Home.Err.Start"), vm.MessageTitle);
        Assert.Equal(L.T("Err.engine_not_running"), vm.MessageText);
        Assert.False(fake.Running);
    }

    [Fact]
    public async Task Check_now_runs_a_probe_and_refreshes_tiles()
    {
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new HomeViewModel(state, null);
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Contains("probe", fake.Calls);
        Assert.False(vm.IsProbing);
        Assert.All(vm.Tiles, t => Assert.Equal(Kind.Ok, t.Kind));
    }

    [Fact]
    public async Task No_probe_yet_shows_enabled_services_as_not_checked()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State(FakeZaprettClient.Scenarios.FirstRun);
        await fake.CallAsync("wizard.apply", new JsonObject { ["services"] = new JsonArray("youtube") });
        await state.RefreshAsync();
        var vm = new HomeViewModel(state, null);
        var tile = Assert.Single(vm.Tiles);
        Assert.Equal(L.T("Home.Tile.NotChecked"), tile.Detail);
        Assert.Equal(L.T("Home.Probe.Never"), vm.ProbeSummary);
    }
}

public sealed class OtherPagesTests
{
    [Fact]
    public async Task Services_apply_sends_the_whole_selection()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new ServicesViewModel(state, null);
        await vm.LoadAsync();
        Assert.False(vm.IsDirty);
        vm.Services.Single(s => s.Id == "telegram").IsSelected = true;
        Assert.True(vm.IsDirty);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Success, vm.MessageKindValue);
        Assert.True(vm.Services.Single(s => s.Id == "telegram").IsEnabledNow);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Strategies_page_shows_engine_strategies_and_test_results()
    {
        L.SetLanguage("en");
        var (state, _, platform) = Make.State();
        await state.RefreshAsync();
        var vm = new StrategiesViewModel(state, null);
        await vm.LoadAsync();
        Assert.Contains(vm.Strategies, s => s.Id == "strategy-general" && s.IsActive);
        Assert.DoesNotContain(vm.Strategies, s => s.Id.StartsWith("z2-", StringComparison.Ordinal));
        Assert.Equal(vm.Strategies[0].Id, "strategy-general");

        await vm.StartTestCommand.ExecuteAsync(null);
        Assert.True(vm.HasResults);
        Assert.Equal(1.0, vm.Results[0].Ratio);
        Assert.Equal(L.T("Test.Mode.Isolated"), vm.ModeText);
        Assert.Contains("1 of 6", vm.BaselineText, StringComparison.Ordinal);
        Assert.Contains(vm.Results, r => r.StatusLabel == L.T("Test.Status.Invalid"));
        Assert.False(string.IsNullOrEmpty(vm.ResultText));

        platform.ConfirmAnswer = false;
        var before = vm.Results.Count;
        await vm.StartTestCommand.ExecuteAsync(null);
        Assert.Equal(before, vm.Results.Count);
    }

    [Fact]
    public async Task Own_strategy_save_rejects_bad_names_and_bad_options()
    {
        L.SetLanguage("en");
        var (state, _, _) = Make.State();
        await state.RefreshAsync();
        var vm = new StrategiesViewModel(state, null);
        await vm.LoadAsync();
        vm.NewStrategyCommand.Execute(null);
        vm.EditorName = "no-prefix";
        await vm.SaveAndCheckCommand.ExecuteAsync(null);
        Assert.Equal(L.T("Strategies.BadName"), vm.MessageTitle);
        Assert.False(vm.HasCheck);

        vm.EditorName = "user-test";
        vm.EditorText = "--filter-tcp=443 --no-such-option=1";
        await vm.SaveAndCheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasCheck);
        Assert.Equal(Kind.Fail, vm.CheckKind);
        Assert.Contains("unrecognized option", vm.EngineOutput, StringComparison.Ordinal);

        vm.EditorText = "--filter-tcp=443 ${hostlists} --dpi-desync=fake";
        await vm.SaveAndCheckCommand.ExecuteAsync(null);
        Assert.Equal(Kind.Ok, vm.CheckKind);
        Assert.True(vm.HasCheck);
        Assert.Equal("user-test", vm.Selected?.Id);
        Assert.Contains(vm.Strategies, s => s.Id == "user-test" && s.IsUser);
    }

    [Fact]
    public async Task Lists_user_list_errors_are_explained_per_line()
    {
        L.SetLanguage("en");
        var (state, _, _) = Make.State();
        await state.RefreshAsync();
        var vm = new ListsViewModel(state, null);
        await vm.LoadAsync();
        Assert.Contains("kinozal.tv", vm.UserText, StringComparison.Ordinal);
        Assert.False(vm.UserDirty);
        vm.UserText = "good.example.org\nбитый домен\n";
        Assert.True(vm.UserDirty);
        await vm.SaveUserCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Error, vm.MessageKindValue);
        Assert.Contains("Line 2", vm.MessageText, StringComparison.Ordinal);
        Assert.True(vm.UserDirty);

        vm.UserText = "good.example.org\n";
        await vm.SaveUserCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Success, vm.MessageKindValue);
        Assert.False(vm.UserDirty);
    }

    [Fact]
    public async Task Lists_toggle_calls_the_service_and_reverts_on_error()
    {
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new ListsViewModel(state, null);
        await vm.LoadAsync();
        var telegram = vm.Domains.Single(d => d.Id == "zaprett-telegram");
        Assert.False(telegram.IsActive);
        telegram.IsActive = true;
        await Task.Delay(200);
        Assert.Contains("list.enable", fake.Calls);
        Assert.True(telegram.IsActive);

        var client = new ScriptedClient((m, _) => m switch
        {
            "list.enable" => Make.Json("""{"ok":false,"error":"not_found"}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state2 = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var vm2 = new ListsViewModel(state2, null);
        vm2.Fill(Make.Json("""{"items":[{"id":"a","type":"list","name":"A","source":"bundle","active":false}]}"""), null);
        vm2.Domains[0].IsActive = true;
        await Task.Delay(50);
        Assert.False(vm2.Domains[0].IsActive);
        Assert.True(vm2.HasMessage);
    }

    [Fact]
    public async Task Lists_subscription_needs_https()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new ListsViewModel(state, null);
        await vm.LoadAsync();
        vm.NewSourceUrl = "http://example.org/list.txt";
        await vm.AddSourceCommand.ExecuteAsync(null);
        Assert.Equal(L.T("Lists.Source.BadUrl"), vm.MessageTitle);
        Assert.DoesNotContain("sources.save", fake.Calls);
        vm.NewSourceUrl = "https://example.org/list.txt?token=SECRET";
        vm.NewSourceTitle = "My list";
        await vm.AddSourceCommand.ExecuteAsync(null);
        var added = vm.Sources.Single(s => s.Name == "my_list");
        Assert.DoesNotContain("SECRET", added.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_summary_report_and_masking()
    {
        L.SetLanguage("en");
        var (state, _, platform) = Make.State(FakeZaprettClient.Scenarios.Stopped);
        await state.RefreshAsync();
        var vm = new DiagnosticsViewModel(state, null);
        await vm.LoadAsync();
        Assert.Equal(L.T("Diag.NotYet"), vm.SummaryTitle);
        await vm.DiagnoseCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);
        Assert.Contains(vm.Rows, r => r.Label == L.T("Verdict.dns_spoof.Label"));
        Assert.True(vm.CanTurnOnDns || vm.SuggestStrategies);
        await vm.CopyReportCommand.ExecuteAsync(null);
        var report = Assert.Single(platform.Copied);
        Assert.Contains("zaprett", report, StringComparison.Ordinal);
        Assert.DoesNotContain("?token", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_summary_counts_and_conclusion()
    {
        L.SetLanguage("en");
        var diag = Make.Json("""{"targets":[{"verdict":"ok"},{"verdict":"throttle"}],"summary":{"verdict":"throttle","counts":{"ok":1,"throttle":1}}}""");
        var s = DiagnoseSummary.From(diag);
        Assert.Equal(L.F("Diag.Conclusion.Some", 1, 2, L.T("Verdict.throttle.Label")), s.Conclusion);
        Assert.Equal(Kind.Warn, s.Kind);
        Assert.Equal(L.T("Diag.NothingToCheck"), DiagnoseSummary.From(Make.Json("""{"targets":[]}""")).Conclusion);
        Assert.Equal(L.T("Diag.Conclusion.Ok"), DiagnoseSummary.From(Make.Json("""{"targets":[{"verdict":"ok"}],"summary":{"verdict":"ok","counts":{"ok":1}}}""")).Conclusion);
    }

    [Fact]
    public void Test_summary_texts()
    {
        L.SetLanguage("en");
        Assert.Equal(L.F("Test.Applied", "strategy-alt3"), TestSummary.Text(Make.Json("""{"state":"done","result":{"applied":"strategy-alt3"}}"""), null));
        Assert.Equal(L.T("Test.Cancelled"), TestSummary.Text(Make.Json("""{"state":"cancelled"}"""), null));
        Assert.Equal(L.T("Test.NoneHelped"), TestSummary.Text(Make.Json("""{"state":"done"}"""), Make.Json("""{"results":[{"id":"a","ok":0,"total":3,"status":"done"}]}""")));
        Assert.Contains("\"a\", 2 of 3", TestSummary.Text(Make.Json("""{"state":"done"}"""), Make.Json("""{"results":[{"id":"a","name":"a","ok":2,"total":3,"status":"done"}]}""")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_send_only_changed_keys()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        Assert.True(vm.IsLoaded);
        Assert.False(vm.IsDirty);
        Assert.Empty(vm.BuildPatch());
        vm.QuicBlock = true;
        vm.MonitorInterval = vm.IntervalChoices.Single(c => c.Value == "15");
        Assert.True(vm.IsDirty);
        var patch = vm.BuildPatch();
        Assert.Equal("""{"main":{"quic_block":true},"monitor":{"interval":15}}""", patch.ToJsonString());
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Success, vm.MessageKindValue);
        Assert.False(vm.IsDirty);
        var saved = (await fake.CallAsync("settings.get")).Obj("settings");
        Assert.True(saved.Obj("main").Bool("quic_block"));
        Assert.Equal(15, saved.Obj("monitor").Int("interval"));
    }

    [Fact]
    public async Task Settings_bad_ports_are_not_sent()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        vm.GameFilter = true;
        vm.GamePortsTcp = "99999";
        var calls = fake.Calls.Count(c => c == "settings.set");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(L.T("Settings.BadPorts"), vm.MessageTitle);
        Assert.Equal(calls, fake.Calls.Count(c => c == "settings.set"));
    }

    [Fact]
    public void Settings_diff_replaces_network_filter_as_a_whole()
    {
        var loaded = Make.Json("""{"main":{"a":1,"network_filter":{"mode":"all","ssids":[],"skip_corporate":false}}}""");
        var wanted = Make.Json("""{"main":{"a":1,"network_filter":{"mode":"ssids","ssids":["Home"],"skip_corporate":false}}}""");
        var patch = SettingsViewModel.Diff(wanted, loaded);
        Assert.Equal("""{"main":{"network_filter":{"mode":"ssids","ssids":["Home"],"skip_corporate":false}}}""", patch.ToJsonString());
    }

    [Fact]
    public async Task Shell_pill_follows_the_state()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State(FakeZaprettClient.Scenarios.Degraded);
        var shell = new ShellViewModel(state);
        Assert.Equal(L.T("Shell.Pill.Connecting"), shell.PillText);
        await state.RefreshAsync();
        Assert.Equal("warn", shell.TrayState);
        fake.SetScenario(FakeZaprettClient.Scenarios.Stopped);
        await state.RefreshAsync();
        Assert.Equal("off", shell.TrayState);
        Assert.Equal(L.T("Shell.Pill.Off"), shell.PillText);
    }
}
