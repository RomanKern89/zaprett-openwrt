using System.Text.Json.Nodes;
using Zaprett.Ipc;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>Regression tests for the findings of the code review of 2026-09-23.</summary>
public sealed class ReviewFixTests
{
    [Fact]
    public async Task Status_event_with_engine_only_does_not_wipe_the_status()
    {
        var (state, _, _) = Make.State();
        await state.RefreshAsync();
        state.ApplyEvent(new ServiceEvent("status", Make.Json("""{"engine":{"instance":"test","state":"exited","restarts":1}}""")));
        Assert.True(state.Status.Bool("enabled"));
        Assert.Equal("strategy-general", state.Status.Obj("strategy").Str("id"));
        Assert.NotEmpty(state.Status.Strings("warnings"));
    }

    [Fact]
    public async Task Status_event_with_running_is_merged_into_the_status()
    {
        var (state, _, _) = Make.State();
        await state.RefreshAsync();
        state.ApplyEvent(new ServiceEvent("status", Make.Json("""{"running":false,"pid":null}""")));
        Assert.False(state.Status.Bool("running"));
        Assert.True(state.Status.Bool("enabled"));
        Assert.Equal("strategy-general", state.Status.Obj("strategy").Str("id"));
    }

    [Fact]
    public async Task Settings_are_read_from_the_settings_field_of_the_service()
    {
        L.SetLanguage("en");
        var client = new ScriptedClient((m, _) => m switch
        {
            "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{"quic_block":true,"engine":"winws2","game_ports_tcp":"80"},"monitor":{"interval":15,"threshold":5}}}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        Assert.True(vm.QuicBlock);
        Assert.Equal("winws2", vm.Engine?.Value);
        Assert.Equal("15", vm.MonitorInterval?.Value);
        Assert.Equal(5, vm.MonitorThreshold);
        Assert.False(vm.IsDirty);
        // values read from the service are not sent back as changes
        var patch = vm.BuildPatch();
        Assert.Null(patch.Obj("main")?.Get("quic_block"));
        Assert.Null(patch.Obj("monitor")?.Get("interval"));
        Assert.Null(patch.Obj("monitor")?.Get("threshold"));
    }

    [Fact]
    public async Task Refused_autostart_switch_goes_back()
    {
        L.SetLanguage("en");
        var client = new ScriptedClient((m, _) => m switch
        {
            "autostart" or "enable" or "disable" => Make.Json("""{"ok":false,"error":"access_denied","message":"нет прав"}"""),
            "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{"enabled":true}}}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        Assert.True(vm.Autostart);
        vm.Autostart = false;
        await Task.Delay(50);
        Assert.True(vm.Autostart);
        Assert.Equal(L.T("Err.access_denied"), vm.MessageText);
    }

    [Fact]
    public void Threshold_nan_keeps_the_loaded_value()
    {
        var (state, _, _) = Make.State();
        var vm = new SettingsViewModel(state, null);
        vm.Load(Make.Json("""{"monitor":{"threshold":4}}"""));
        vm.MonitorThreshold = double.NaN;
        // NaN is replaced by the loaded 4, which is no change, so threshold is not sent at all
        Assert.Null(vm.BuildPatch().Obj("monitor")?.Get("threshold"));
        vm.MonitorThreshold = 7;
        Assert.Equal(7, vm.BuildPatch().Obj("monitor").Int("threshold"));
    }

    [Fact]
    public void Subscription_names_do_not_collide()
    {
        Assert.Equal("source", ListsViewModel.UniqueSourceName("source", ["other"]));
        Assert.Equal("source_2", ListsViewModel.UniqueSourceName("source", ["source"]));
        Assert.Equal("source_3", ListsViewModel.UniqueSourceName("source", ["source", "source_2"]));
        var longName = new string('a', 32);
        var unique = ListsViewModel.UniqueSourceName(longName, [longName]);
        Assert.Equal(32, unique.Length);
        Assert.EndsWith("_2", unique, StringComparison.Ordinal);
        Assert.True(Validation.IsSourceName(unique));
    }

    [Fact]
    public void Masking_covers_user_info_and_upper_case()
    {
        var masked = Validation.MaskUrls("HTTPS://user:SECRET@host/list?Token=X and https://h/p?q=Y");
        Assert.DoesNotContain("SECRET", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("Token=X", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("q=Y", masked, StringComparison.Ordinal);
        Assert.Contains("host/list?…", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Reused_combo_box_null_does_not_reset_the_variant()
    {
        L.SetLanguage("en");
        var preset = Make.Json("""{"id":"discord","name":"Discord","works":"yes","lists":["a"],"variants":[{"id":"full","name":"Full","lists":["b"]}]}""");
        var choice = new ServiceChoice(preset, null);
        choice.SelectedVariant = choice.Variants[1];
        choice.SelectedVariant = null;
        Assert.Equal("full", choice.SelectedVariant?.Id);
        Assert.Equal("discord:full", choice.Reference);
    }

    [Fact]
    public void Time_accepts_milliseconds_and_rejects_nonsense()
    {
        L.SetLanguage("en");
        Assert.Equal(UiText.Time(1_800_000_000), UiText.Time(1_800_000_000_000));
        Assert.Equal("never", UiText.Time(long.MaxValue));
        Assert.Equal("never", UiText.Ago(-5, DateTimeOffset.Now));
        Assert.NotEmpty(UiText.Duration(long.MaxValue));
    }

    [Fact]
    public void Broken_translation_placeholder_does_not_throw()
    {
        L.SetLanguage("en");
        // the template has three placeholders, one argument is given: the raw template comes back instead of an exception
        var text = L.F("Monitor.HistorySummary", 1);
        Assert.Contains("{2}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_page_part_is_not_taken()
    {
        var page = Make.Json("""{"ok":true,"items":{"ok":false,"error":"internal_error"},"test":{"ok":true,"running":false}}""");
        Assert.Null(page.OkPart("items"));
        Assert.NotNull(page.OkPart("test"));
        Assert.Null(page.OkPart("missing"));
    }

    [Fact]
    public async Task Subscription_switch_is_sent_for_its_own_item_and_reverted_on_failure()
    {
        L.SetLanguage("en");
        var sources = Make.Json("""{"ok":true,"sources":[{"name":"a","title":"A","type":"list","url":"https://a/x","enabled":false},{"name":"b","title":"B","type":"list","url":"https://b/x","enabled":false}]}""");
        var client = new ScriptedClient((m, a) => m switch
        {
            "sources.list" => sources,
            "sources.save" => a.Str("name") == "b" ? Make.Json("""{"ok":false,"error":"write_failed"}""") : Make.Json("""{"ok":true}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var vm = new ListsViewModel(state, null);
        vm.Fill(Make.Json("""{"items":[]}"""), sources);
        vm.Sources[0].IsEnabled = true;
        await Task.Delay(50);
        var save = client.Calls.Single(c => c.Method == "sources.save");
        Assert.Equal("a", save.Args.Str("name"));
        Assert.True(save.Args.Bool("enabled"));

        var b = vm.Sources.Single(s => s.Name == "b");
        b.IsEnabled = true;
        await Task.Delay(50);
        Assert.False(b.IsEnabled);
        Assert.True(vm.HasMessage);
    }

    [Theory]
    [InlineData("ru", "Обход выключен")]
    [InlineData("en", "The bypass is off")]
    [InlineData("zh-CN", "绕过已关闭")]
    public async Task Every_call_carries_the_interface_language_and_the_fake_answers_in_it(string lang, string expected)
    {
        var (state, _, _) = Make.State(FakeZaprettClient.Scenarios.Stopped);
        L.SetLanguage(lang);
        try
        {
            var ex = await Assert.ThrowsAsync<ZaprettCallException>(() => state.CallAsync("restart"));
            Assert.Equal("disabled", ex.Code);
            Assert.StartsWith(expected, ex.ServiceMessage, StringComparison.Ordinal);
            // the person sees the interface text for a known code, not the service message
            Assert.Equal(L.T("Err.disabled"), ex.Text);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Fact]
    public async Task Language_switch_saves_ui_language_in_the_service_and_switches_the_interface()
    {
        L.SetLanguage("ru");
        var client = new ScriptedClient((m, _) => m switch
        {
            "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{},"ui":{"language":"ru"}}}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        try
        {
            Assert.Equal("ru", vm.Language?.Value);
            vm.Language = vm.Languages.Single(l => l.Value == "zh-CN");
            for (var i = 0; i < 100 && L.Language != "zh-CN"; i++)
                await Task.Delay(10);
            var set = Assert.Single(client.Calls, c => c.Method == "settings.set");
            Assert.Equal("zh-CN", set.Args.Obj("ui").Str("language"));
            Assert.Equal("zh-CN", L.Language);
            Assert.Equal("zh-CN", state.Prefs.Language);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }
}
