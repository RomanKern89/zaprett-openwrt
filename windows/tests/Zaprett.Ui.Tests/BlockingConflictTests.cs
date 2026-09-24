using Zaprett.Ipc;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>
/// Another program with the same traffic (another zapret, GoodbyeDPI, a foreign WinDivert driver, a foreign winws
/// through the loaded driver): the internet may be down while the engine "runs". Format of wincore: warning
/// "conflict_blocking" and conflicts_blocking [{id, name, detail, kind, service, path, pid}] (missing ones null);
/// the same items in the results of the automatic selection and of monitor.run; detail/fix of the service are English
/// and the interface builds its own texts by id and kind.
/// </summary>
public sealed class BlockingConflictTests
{
    private const string WinwsPath = @"C:\Users\user\Downloads\zapret-discord-youtube\bin\winws.exe";

    private const string Blocked = """
        {"ok":true,"enabled":true,"running":true,"warnings":["conflict_blocking"],
         "conflicts_blocking":[{"id":"foreign_windivert_user","name":"winws (WinDivert)","detail":"Process winws (pid 4312) ...","kind":"process",
                                "service":null,"path":"C:\\Users\\user\\Downloads\\zapret-discord-youtube\\bin\\winws.exe","pid":4312}]}
        """;

    private const string Working = """{"ok":true,"enabled":true,"running":true,"warnings":[],"conflicts_blocking":[]}""";

    [Fact]
    public void Foreign_winws_through_the_loaded_driver_says_close_the_program_and_its_process()
    {
        L.SetLanguage("ru");
        var c = BlockingConflict.FromStatus(Make.Json(Blocked))!;
        Assert.Equal("foreign_windivert_user", c.Id);
        // home: the file stands on its own line, the advice does not repeat it
        Assert.Equal(L.F("Conflict.Fix.processHere", "4312"), c.Advice);
        Assert.DoesNotContain(WinwsPath, c.Advice, StringComparison.Ordinal);
        Assert.Contains("завершите процесс 4312", c.Advice, StringComparison.Ordinal);
        Assert.Equal(L.F("Conflict.Where.PathPid", WinwsPath, "4312"), c.Where);
    }

    [Fact]
    public void English_texts_of_the_service_are_replaced_by_id_and_kind()
    {
        L.SetLanguage("ru");
        var svc = Make.Json("""
            {"id":"goodbyedpi","name":"GoodbyeDPI","severity":"block","kind":"service","service":"GoodbyeDPI","path":null,"pid":null,
             "detail":"Service \"GoodbyeDPI\" is running: two DPI bypass programs on one PC break each other","fix":"Stop and disable the service"}
            """);
        Assert.Equal(L.F("Conflict.Detail.service", "GoodbyeDPI", L.T("Conflict.Why.goodbyedpi")), ConflictText.Detail(svc));
        Assert.Equal(L.F("Conflict.Fix.service", "GoodbyeDPI"), ConflictText.Fix(svc));
        Assert.DoesNotContain("Stop and disable", ConflictText.Fix(svc), StringComparison.Ordinal);

        var driver = Make.Json("""{"id":"foreign_windivert","name":"WinDivert (WinDivert14)","kind":"driver","service":"WinDivert14","path":"C:\\gdpi\\WinDivert64.sys","pid":null,"detail":"Another program's driver","fix":"Close it"}""");
        Assert.Equal(L.F("Conflict.Detail.driver", "WinDivert14", "C:\\gdpi\\WinDivert64.sys"), ConflictText.Detail(driver));
        Assert.Equal(L.F("Conflict.Fix.driver", "C:\\gdpi\\WinDivert64.sys"), ConflictText.Fix(driver));

        var proc = Make.Json("""{"id":"zapret","name":"zapret","kind":"process","service":null,"path":"C:\\z\\winws.exe","pid":77,"detail":"x","fix":"y"}""");
        Assert.Equal(L.F("Conflict.Detail.process", "C:\\z\\winws.exe", "77", L.T("Conflict.Why.zapret")), ConflictText.Detail(proc));
        Assert.Equal(L.F("Conflict.Fix.process", "C:\\z\\winws.exe", "77"), ConflictText.Fix(proc));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Every_language_has_the_texts(string lang)
    {
        L.SetLanguage(lang);
        try
        {
            var c = BlockingConflict.FromStatus(Make.Json(Blocked))!;
            Assert.DoesNotContain("Conflict.", c.Advice, StringComparison.Ordinal);
            Assert.DoesNotContain("Conflict.", c.Where, StringComparison.Ordinal);
            Assert.DoesNotContain("Warn.", UiText.Warning("conflict_blocking").Title, StringComparison.Ordinal);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Fact]
    public void Wizard_and_diagnostics_keep_the_path_in_the_advice()
    {
        L.SetLanguage("ru");
        var st = Make.Json(Blocked);
        var item = ConflictItem.From((System.Text.Json.Nodes.JsonObject)st["conflicts_blocking"]![0]!.DeepClone());
        Assert.Equal(L.F("Conflict.Fix.process", WinwsPath, "4312"), item.Fix);
        // negative control of the home form: without a path there is nothing to drop
        var noPath = Make.Json("""{"id":"x","name":"X","kind":"process","service":null,"path":null,"pid":5}""");
        Assert.Equal(L.F("Conflict.Fix.process", "X", "5"), ConflictText.Fix(noPath, withPath: false));
    }

    [Fact]
    public void Advice_follows_the_kind_for_any_id()
    {
        L.SetLanguage("ru");
        var svc = Make.Json("""{"id":"something_new","name":"New","kind":"service","service":"NewSvc","path":null,"pid":null,"detail":"d","fix":"English fix"}""");
        Assert.Equal(L.F("Conflict.Fix.service", "NewSvc"), ConflictText.Fix(svc));
        var drv = Make.Json("""{"id":"other_driver","name":"X","kind":"driver","service":"XDrv","path":"C:\\x\\x.sys","pid":null,"detail":"d","fix":"English fix"}""");
        Assert.Equal(L.F("Conflict.Fix.driver", "C:\\x\\x.sys"), ConflictText.Fix(drv));
        var proc = Make.Json("""{"id":"something_else","name":"Y","kind":"process","service":null,"path":"C:\\y\\y.exe","pid":9,"detail":"d","fix":"English fix"}""");
        Assert.Equal(L.F("Conflict.Fix.process", "C:\\y\\y.exe", "9"), ConflictText.Fix(proc));
        // the process advice offers both: close the program or end the process
        Assert.Contains("завершите процесс 9", ConflictText.Fix(proc), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_findings_keep_the_text_of_the_service()
    {
        var vpn = Make.Json("""{"id":"vpn:Wintun","name":"VPN: Wintun","severity":"info","kind":null,"service":null,"path":null,"pid":null,"detail":"VPN adapter","fix":"Disconnect"}""");
        Assert.Equal("VPN adapter", ConflictText.Detail(vpn));
        Assert.Equal("Disconnect", ConflictText.Fix(vpn));
        Assert.Equal("", ConflictText.Where(vpn));
    }

    [Fact]
    public void Status_without_the_warning_and_with_an_empty_list_has_no_conflict()
    {
        Assert.Null(BlockingConflict.FromStatus(Make.Json(Working)));
        Assert.Null(BlockingConflict.FromStatus(null));
        // the "conflicts" method: only severity block counts there
        Assert.Null(BlockingConflict.From(Make.Json("""{"id":"adguard","name":"AdGuard","severity":"warn","kind":"service","service":"AdGuard Service"}""")));
        Assert.NotNull(BlockingConflict.From(Make.Json("""{"id":"zapret","name":"zapret","severity":"block","kind":"service","service":"zapret"}""")));
    }

    [Fact]
    public void The_warning_alone_still_counts()
    {
        L.SetLanguage("ru");
        var c = BlockingConflict.FromStatus(Make.Json("""{"running":true,"warnings":["conflict_blocking"],"conflicts_blocking":[]}"""));
        Assert.NotNull(c);
        Assert.Equal(L.T("Conflict.UnknownProgram"), c!.Name);
        Assert.Equal(L.T("Conflict.Fix.unknown"), c.Advice);
    }

    [Fact]
    public void Home_is_red_with_the_program_where_it_is_and_the_advice()
    {
        L.SetLanguage("ru");
        var st = Make.Json(Blocked);
        var c = BlockingConflict.FromStatus(st)!;
        // outranks "running", the automatic selection and waiting for a network
        var hero = HomeViewModel.Hero(st, true, true, null, true, true, c);
        Assert.Equal(Kind.Fail, hero.Kind);
        Assert.Equal("Обходу мешает другая программа: winws (WinDivert)", hero.Title);
        Assert.Contains(c.Advice, hero.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_page_of_the_fake_blocked_scenario()
    {
        L.SetLanguage("ru");
        var (state, _, _) = Make.State(FakeZaprettClient.Scenarios.Blocked);
        await state.RefreshAsync();
        var vm = new HomeViewModel(state, null);
        await vm.LoadAsync();
        Assert.Equal(Kind.Fail, vm.HeroKind);
        Assert.Equal("Обходу мешает другая программа: winws (WinDivert)", vm.HeroTitle);
        Assert.True(vm.HasBlockingConflict);
        Assert.Contains("winws.exe", vm.ConflictWhere, StringComparison.Ordinal);
        // the hero says it already: no second (yellow) card of the same warning
        Assert.DoesNotContain(vm.Warnings, w => w.Title == L.T("Warn.conflict_blocking.Title"));
    }

    [Fact]
    public void Negative_control_without_a_blocking_find_home_stays_green()
    {
        var st = Make.Json(Working);
        Assert.Equal(Kind.Ok, HomeViewModel.Hero(st, true, true, null, false, false, BlockingConflict.FromStatus(st)).Kind);
    }

    [Fact]
    public void Warning_card_leads_to_diagnostics_and_is_not_informational()
    {
        L.SetLanguage("ru");
        var w = UiText.Warning("conflict_blocking");
        Assert.Equal("Обходу мешает другая программа", w.Title);
        Assert.Equal("diagnostics", w.Action);
        Assert.False(w.IsInfo);
    }

    [Fact]
    public void Title_bar_and_tray_show_an_error()
    {
        L.SetLanguage("ru");
        var state = new AppState(new ScriptedClient((_, _) => Make.Json("""{"ok":true}""")), new FakePlatform(), UiPrefs.Load(null));
        var shell = new ShellViewModel(state);
        state.Connection = ConnectionState.Available;
        state.Status = Make.Json(Blocked);
        shell.Update();
        Assert.Equal("error", shell.TrayState);
        Assert.Equal(Kind.Fail, shell.PillKind);
        Assert.Equal(L.T("Shell.Pill.Conflict"), shell.PillText);
        state.Status = Make.Json(Working);
        shell.Update();
        Assert.Equal("on", shell.TrayState);
    }

    [Fact]
    public void Monitor_says_the_repair_was_not_started_and_so_does_the_notification()
    {
        L.SetLanguage("ru");
        Assert.True(UiText.RepairBlockedByConflict(Make.Json("""{"state":"degraded","repair_started":false,"repair_blocked":"conflict_blocking"}""")));
        Assert.False(UiText.RepairBlockedByConflict(Make.Json("""{"state":"degraded","repair_blocked":null}""")));
        Assert.False(UiText.RepairBlockedByConflict(Make.Json("""{"state":"degraded","repair_started":true}""")));

        var platform = new FakePlatform();
        var state = new AppState(new ScriptedClient((_, _) => Make.Json("""{"ok":true}""")), platform, UiPrefs.Load(null));
        state.ApplyEvent(new ServiceEvent("monitor", Make.Json("""{"monitor":{"state":"ok","auto_repair":true}}""")));
        state.ApplyEvent(new ServiceEvent("monitor", Make.Json("""{"monitor":{"state":"degraded","auto_repair":true,"repair_started":false,"repair_blocked":"conflict_blocking"}}""")));
        var toast = Assert.Single(platform.Notifications);
        Assert.Equal(L.T("Toast.Degraded.TextConflict"), toast.Text);
    }

    [Fact]
    public async Task Manual_selection_warns_first_and_cancel_starts_nothing()
    {
        L.SetLanguage("ru");
        var platform = new FakePlatform { ConfirmAnswer = false };
        var client = new ScriptedClient((m, _) => m == "status" ? Make.Json(Blocked) : Make.Json("""{"ok":true}"""));
        var state = new AppState(client, platform, UiPrefs.Load(null)) { Status = Make.Json(Blocked) };
        var vm = new StrategiesViewModel(state, null);
        await vm.StartTestCommand.ExecuteAsync(null);
        var ask = Assert.Single(platform.Confirms);
        Assert.Equal(L.T("Test.Conflict.Anyway"), ask.Primary);
        Assert.Contains("winws (WinDivert)", ask.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(client.Calls, c => c.Method == "test.start");

        var wizard = new WizardViewModel(state, null);
        await wizard.AutoSelectCommand.ExecuteAsync(null);
        Assert.Equal(2, platform.Confirms.Count);
        Assert.DoesNotContain(client.Calls, c => c.Method == "test.start");
    }

    [Fact]
    public async Task Without_a_conflict_there_is_no_extra_question()
    {
        var platform = new FakePlatform { ConfirmAnswer = false };
        var state = new AppState(new ScriptedClient((_, _) => Make.Json("""{"ok":true}""")), platform, UiPrefs.Load(null)) { Status = Make.Json(Working) };
        var vm = new StrategiesViewModel(state, null);
        await vm.StartTestCommand.ExecuteAsync(null);
        Assert.DoesNotContain(platform.Confirms, c => c.Primary == L.T("Test.Conflict.Anyway"));
    }

    [Fact]
    public void Results_taken_during_a_conflict_are_marked()
    {
        L.SetLanguage("ru");
        var job = Make.Json("""{"state":"done","result":{"conflict_blocking":true,"conflicts_blocking":[{"id":"zapret","name":"zapret (Flowseal)","detail":"d","kind":"service","service":"zapret","path":null,"pid":null}]}}""");
        var note = TestSummary.ConflictNote(job, null);
        Assert.Equal(L.F("Test.ConflictNote", "zapret (Flowseal)"), note);
        Assert.Equal(note, TestSummary.ConflictNote(null, Make.Json("""{"conflicts_blocking":[{"id":"zapret","name":"zapret (Flowseal)","kind":"service","service":"zapret"}]}""")));
        Assert.Equal(L.F("Test.ConflictNote", L.T("Conflict.UnknownProgram")), TestSummary.ConflictNote(Make.Json("""{"result":{"conflict_blocking":true}}"""), null));
        Assert.Equal("", TestSummary.ConflictNote(Make.Json("""{"result":{"conflict_blocking":false,"conflicts_blocking":[]}}"""), Make.Json("""{"conflicts_blocking":[]}""")));
    }

    [Fact]
    public void Strategies_page_shows_the_note_with_saved_results()
    {
        L.SetLanguage("ru");
        var state = new AppState(new ScriptedClient((_, _) => Make.Json("""{"ok":true}""")), new FakePlatform(), UiPrefs.Load(null));
        var vm = new StrategiesViewModel(state, null);
        vm.ShowTest(Make.Json("""{"ok":true,"results":{"results":[],"conflicts_blocking":[{"id":"goodbyedpi","name":"GoodbyeDPI","kind":"service","service":"GoodbyeDPI"}]}}"""));
        Assert.Contains("GoodbyeDPI", vm.ConflictNote, StringComparison.Ordinal);
        vm.ShowTest(Make.Json("""{"ok":true,"results":{"results":[],"conflicts_blocking":[]}}"""));
        Assert.Equal("", vm.ConflictNote);
    }

    [Fact]
    public void Wizard_card_uses_the_interface_texts_and_shows_the_file()
    {
        L.SetLanguage("ru");
        var item = ConflictItem.From(Make.Json("""
            {"id":"goodbyedpi","name":"GoodbyeDPI","severity":"block","kind":"service","service":"GoodbyeDPI",
             "path":"C:\\gdpi\\goodbyedpi.exe","pid":3120,"detail":"Service \"GoodbyeDPI\" is running","fix":"Stop and disable"}
            """));
        Assert.Equal(Kind.Fail, item.Kind);
        Assert.Equal(L.F("Conflict.Detail.service", "GoodbyeDPI", L.T("Conflict.Why.goodbyedpi")), item.Detail);
        Assert.Equal(L.F("Conflict.Fix.service", "GoodbyeDPI"), item.Fix);
        Assert.Equal(L.F("Conflict.Where.PathPid", "C:\\gdpi\\goodbyedpi.exe", "3120"), item.Where);
    }
}
