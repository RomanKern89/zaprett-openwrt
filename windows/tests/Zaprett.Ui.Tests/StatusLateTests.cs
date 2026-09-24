using System.Text.Json.Nodes;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>
/// D18: pages are created before the first status, and the shared state is refreshed with "page", whose status
/// part has no tray_autostart (the service adds it to the answer of "status" only). Whatever depends on the status
/// must follow it when it comes.
/// </summary>
public sealed class StatusLateTests
{
    private const string PageStatus = """{"ok":true,"enabled":true,"running":true,"can_modify":true}""";

    private static ScriptedClient Service(Func<string> pageStatus, string status) => new((m, _) => m switch
    {
        "page" => Make.Json("{\"ok\":true,\"status\":" + pageStatus() + "}"),
        "status" => Make.Json(status),
        "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{},"ui":{"language":"ru"}}}"""),
        "update.check" => Make.Json("""{"ok":false,"error":"not_supported"}"""),
        _ => Make.Json("""{"ok":true}"""),
    });

    [Fact]
    public async Task The_icon_switch_shows_when_only_the_answer_of_status_has_the_field()
    {
        L.SetLanguage("ru");
        var client = Service(() => PageStatus, """{"ok":true,"enabled":true,"running":true,"can_modify":true,"tray_autostart":true}""");
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var vm = new SettingsViewModel(state, null);   // created before any status
        await state.RefreshAsync();                     // the shared state gets the status of "page"
        Assert.False(state.Status!.ContainsKey("tray_autostart"));
        await vm.LoadAsync();
        Assert.True(vm.TrayAutostartSupported);
        Assert.True(vm.TrayAutostart);
        Assert.True(vm.CanChangeTrayAutostart);
        // later refreshes (still without the field) do not hide it again
        await state.RefreshAsync();
        Assert.True(vm.TrayAutostartSupported);
        Assert.True(vm.TrayAutostart);
    }

    [Fact]
    public async Task The_page_opened_while_the_service_is_still_starting_shows_the_switch_once_it_answers()
    {
        L.SetLanguage("ru");
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01, SlowStartDelay = TimeSpan.FromMilliseconds(400) };
        fake.SetScenario(FakeZaprettClient.Scenarios.SlowStart);
        var state = new AppState(fake, new FakePlatform(), UiPrefs.Load(null));
        var vm = new SettingsViewModel(state, null);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var load = vm.LoadAsync();
        var refresh = state.RefreshAsync();
        Assert.Null(state.Status);
        Assert.False(vm.TrayAutostartSupported);
        await Task.WhenAll(load, refresh);
        Assert.True(vm.TrayAutostartSupported);
        Assert.True(vm.TrayAutostart);
        Assert.Contains(nameof(SettingsViewModel.TrayAutostartSupported), changed);
    }

    [Fact]
    public async Task Lists_built_before_the_status_lock_when_the_rights_arrive()
    {
        var pageStatus = PageStatus;
        var client = Service(() => pageStatus, PageStatus);
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var lists = new ListsViewModel(state, null);
        lists.Fill(Make.Json("""{"ok":true,"items":[{"id":"zaprett-youtube","type":"list","source":"preset","active":true}]}"""),
            Make.Json("""{"ok":true,"sources":[{"name":"s1","url":"https://example.org/l.txt","enabled":true}]}"""));
        var item = Assert.Single(lists.Domains);
        var source = Assert.Single(lists.Sources);
        Assert.True(item.CanToggle);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pageStatus = """{"ok":true,"enabled":true,"running":true,"can_modify":false}""";
        await state.RefreshAsync();
        Assert.False(item.CanToggle);
        Assert.False(source.CanModify);
        Assert.Contains(nameof(ListItem.CanToggle), changed);
    }

    [Fact]
    public async Task The_fake_page_has_no_tray_autostart_like_the_service()
    {
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        var page = await fake.CallAsync("page", new JsonObject { ["name"] = "overview" });
        Assert.False(page["status"]!.AsObject().ContainsKey("tray_autostart"));
        Assert.True((await fake.CallAsync("status")).ContainsKey("tray_autostart"));
    }
}
