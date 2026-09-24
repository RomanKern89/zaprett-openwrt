using System.Text.Json.Nodes;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>The notification area as the shell behaves: one icon per (window, id); NIM_ADD of an existing icon and
/// NIM_MODIFY of a missing one fail.</summary>
internal sealed class FakeNotifyArea : INotifyArea
{
    public int Icons { get; set; }

    public int Adds { get; private set; }

    public bool Add()
    {
        Adds++;
        if (Icons > 0)
            return false;
        Icons = 1;
        return true;
    }

    public bool SetVersion() => Icons > 0;

    public bool Modify() => Icons > 0;

    public bool Delete()
    {
        if (Icons == 0)
            return false;
        Icons = 0;
        return true;
    }

    /// <summary>Explorer restarted: every icon of every program is gone.</summary>
    public void ExplorerRestarted() => Icons = 0;
}

public sealed class TrayRegistrationTests
{
    [Fact]
    public void Create_twice_keeps_one_icon()
    {
        var area = new FakeNotifyArea();
        var tray = new TrayRegistration(area);
        tray.Create();
        tray.Create();
        Assert.Equal(1, area.Icons);
        Assert.Equal(1, area.Adds);
        Assert.True(tray.IsAdded);
    }

    [Fact]
    public void Explorer_restart_brings_back_exactly_one_icon()
    {
        var area = new FakeNotifyArea();
        var tray = new TrayRegistration(area);
        tray.Create();
        area.ExplorerRestarted();
        tray.OnTaskbarCreated();
        Assert.Equal(1, area.Icons);
        Assert.True(tray.IsAdded);
    }

    [Fact]
    public void Taskbar_recreated_while_the_icon_exists_keeps_one_and_stays_updatable()
    {
        // TaskbarCreated also comes when only the taskbar is re-created (DPI change): the icon is still there
        var area = new FakeNotifyArea();
        var tray = new TrayRegistration(area);
        tray.Create();
        tray.OnTaskbarCreated();
        Assert.Equal(1, area.Icons);
        Assert.True(tray.IsAdded);
        tray.Update();
        Assert.Equal(1, area.Icons);
    }

    [Fact]
    public void Several_taskbar_messages_in_a_row_never_give_two()
    {
        var area = new FakeNotifyArea();
        var tray = new TrayRegistration(area);
        tray.Create();
        for (var i = 0; i < 5; i++)
        {
            if (i % 2 == 0)
                area.ExplorerRestarted();
            tray.OnTaskbarCreated();
            Assert.Equal(1, area.Icons);
        }
    }

    [Fact]
    public void An_icon_lost_silently_is_added_again_on_the_next_update()
    {
        var area = new FakeNotifyArea();
        var tray = new TrayRegistration(area);
        tray.Create();
        area.ExplorerRestarted(); // no TaskbarCreated arrived
        tray.Update();
        Assert.Equal(1, area.Icons);
        Assert.True(tray.IsAdded);
    }

    [Fact]
    public void Exit_removes_the_icon_and_nothing_brings_it_back()
    {
        var area = new FakeNotifyArea();
        var tray = new TrayRegistration(area);
        tray.Create();
        tray.Dispose();
        Assert.Equal(0, area.Icons);
        tray.Update();
        tray.OnTaskbarCreated();
        tray.Create();
        Assert.Equal(0, area.Icons);
    }
}

public sealed class ActivationPolicyTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("--page settings", true)]
    [InlineData("--tray", false)]
    [InlineData("\"C:\\Program Files\\zaprett\\ui\\zaprett-ui.exe\" --tray", false)]
    [InlineData("--minimized", false)]
    [InlineData("\"--tray\"", false)]
    public void Second_start_shows_the_window_only_without_tray_arguments(string? arguments, bool shows)
    {
        Assert.Equal(shows, ActivationPolicy.ShowsWindow(arguments));
    }
}

public sealed class TrayAutostartSettingTests
{
    private static async Task<(SettingsViewModel Vm, ScriptedClient Client, FakePlatform Platform)> Load(string status, Func<string, JsonObject?, JsonObject>? answer = null)
    {
        L.SetLanguage("ru");
        var client = new ScriptedClient((m, a) => answer?.Invoke(m, a) ?? m switch
        {
            "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{}}}"""),
            "update.check" => Make.Json("""{"ok":false,"error":"not_supported"}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var platform = new FakePlatform();
        var state = new AppState(client, platform, UiPrefs.Load(null)) { Status = Make.Json(status) };
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        return (vm, client, platform);
    }

    [Fact]
    public async Task The_switch_shows_the_registry_as_the_service_reports_it()
    {
        var (vm, _, _) = await Load("""{"enabled":true,"running":true,"tray_autostart":false}""");
        Assert.True(vm.TrayAutostartSupported);
        Assert.False(vm.TrayAutostart);
        Assert.True(vm.CanChangeTrayAutostart);
    }

    [Fact]
    public async Task Turning_it_on_asks_the_service_and_takes_its_answer()
    {
        var (vm, client, _) = await Load("""{"enabled":true,"running":true,"tray_autostart":false}""",
            (m, _) => m == "tray.autostart" ? Make.Json("""{"ok":true,"tray_autostart":true}""") : null!);
        vm.TrayAutostart = true;
        await Task.Delay(50);
        var call = Assert.Single(client.Calls, c => c.Method == "tray.autostart");
        Assert.True(call.Args!["enable"]!.GetValue<bool>());
        Assert.True(vm.TrayAutostart);
    }

    [Fact]
    public async Task Without_the_rights_the_switch_returns_and_explains_who_may_change_it()
    {
        var (vm, _, _) = await Load("""{"enabled":true,"running":true,"tray_autostart":true}""",
            (m, _) => m == "tray.autostart" ? Make.Json("""{"ok":false,"error":"access_denied","message":"denied"}""") : null!);
        vm.TrayAutostart = false;
        await Task.Delay(50);
        Assert.True(vm.TrayAutostart);
        Assert.True(vm.TrayAutostartDenied);
        Assert.False(vm.CanChangeTrayAutostart);
        Assert.Equal(L.T("Settings.TrayAutostartDenied"), vm.TrayAutostartHint);
        Assert.True(vm.HasMessage);
    }

    [Fact]
    public async Task A_service_without_the_field_shows_no_switch()
    {
        var (vm, _, _) = await Load("""{"enabled":true,"running":true}""");
        Assert.False(vm.TrayAutostartSupported);
    }

    [Fact]
    public async Task An_unreadable_registry_shows_the_switch_locked_with_its_reason()
    {
        // winsvc: null only when the registry could not be read
        var (vm, _, _) = await Load("""{"enabled":true,"running":true,"tray_autostart":null}""");
        Assert.True(vm.TrayAutostartSupported);
        Assert.True(vm.TrayAutostartUnknown);
        Assert.False(vm.CanChangeTrayAutostart);
        Assert.Equal(L.T("Settings.TrayAutostartUnknown"), vm.TrayAutostartHint);
    }

    [Fact]
    public async Task The_fake_service_keeps_the_value()
    {
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        Assert.True((await fake.CallAsync("status"))["tray_autostart"]!.GetValue<bool>());
        await fake.CallAsync("tray.autostart", new JsonObject { ["enable"] = false });
        Assert.False((await fake.CallAsync("status"))["tray_autostart"]!.GetValue<bool>());
        var bad = await fake.CallAsync("tray.autostart", new JsonObject { ["enable"] = "yes" });
        Assert.False(bad["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_argument", bad["error"]!.GetValue<string>());
    }
}
