using System.Text.Json.Nodes;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>
/// "Turn the bypass on when Windows starts" (main.autostart) is separate from "on now" (status.enabled): the switch
/// changes only the behaviour after a restart, the power button only the bypass now.
/// </summary>
public sealed class AutostartTests
{
    private static readonly string[] NowMethods = ["start", "stop", "enable", "disable", "restart"];

    private static async Task<(SettingsViewModel Vm, ScriptedClient Client)> Settings(string status, bool serviceHasAutostart = true)
    {
        L.SetLanguage("ru");
        var client = new ScriptedClient((m, a) => m switch
        {
            "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{}}}"""),
            "update.check" => Make.Json("""{"ok":false,"error":"not_supported"}"""),
            "status" => Make.Json(status),
            "autostart" when !serviceHasAutostart => Make.Json("""{"ok":false,"error":"unknown_method","message":"Unknown method"}"""),
            "autostart" => Make.Json($$"""{"ok":true,"autostart":{{(a!["enable"]!.GetValue<bool>() ? "true" : "false")}},"enabled":true}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null)) { Status = Make.Json(status) };
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        return (vm, client);
    }

    [Fact]
    public async Task The_switch_shows_autostart_not_the_state_now()
    {
        var (vm, _) = await Settings("""{"ok":true,"enabled":true,"running":true,"autostart":false,"autostart_separate":true}""");
        Assert.False(vm.Autostart);
        var (vm2, _) = await Settings("""{"ok":true,"enabled":false,"running":false,"autostart":true,"autostart_separate":true}""");
        Assert.True(vm2.Autostart);
    }

    [Fact]
    public async Task Turning_the_switch_off_changes_only_autostart()
    {
        var (vm, client) = await Settings("""{"ok":true,"enabled":true,"running":true,"autostart":true,"autostart_separate":true}""");
        vm.Autostart = false;
        await Task.Delay(50);
        var call = Assert.Single(client.Calls, c => c.Method == "autostart");
        Assert.False(call.Args!["enable"]!.GetValue<bool>());
        Assert.DoesNotContain(client.Calls, c => NowMethods.Contains(c.Method));
        Assert.False(vm.Autostart);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public async Task An_older_service_without_the_method_gets_enable_and_disable_as_before()
    {
        // no status.autostart_separate: the old meaning, and the new method is not even tried
        var (vm, client) = await Settings("""{"ok":true,"enabled":true,"running":true,"autostart":true}""", serviceHasAutostart: false);
        vm.Autostart = false;
        await Task.Delay(50);
        Assert.DoesNotContain(client.Calls, c => c.Method == "autostart");
        Assert.Contains(client.Calls, c => c.Method == "disable");
        Assert.False(vm.Autostart);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public async Task Without_a_status_yet_the_method_is_tried_and_an_older_service_falls_back()
    {
        var client = new ScriptedClient((m, _) => m == "autostart"
            ? Make.Json("""{"ok":false,"error":"unknown_method"}""")
            : Make.Json("""{"ok":true}"""));
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        Assert.False(await state.SetAutostartAsync(false));
        Assert.Equal(["autostart", "disable"], client.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task With_the_fake_service_the_bypass_keeps_running()
    {
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        Assert.True(vm.Autostart);
        vm.Autostart = false;
        for (var i = 0; i < 100 && fake.Autostart; i++)
            await Task.Delay(10);
        Assert.False(fake.Autostart);
        Assert.True(fake.Running);
        Assert.True(fake.Enabled);
    }

    [Fact]
    public async Task The_power_button_changes_the_bypass_now_and_leaves_autostart()
    {
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var home = new HomeViewModel(state, null);
        await home.ToggleCommand.ExecuteAsync(null);
        Assert.False(fake.Running);
        Assert.True(fake.Autostart);
        Assert.DoesNotContain(fake.Calls, c => c is "autostart" or "enable" or "disable");
        // on now, but not at the next start of Windows: turning on must not change that
        await fake.CallAsync("autostart", new JsonObject { ["enable"] = false });
        await home.ToggleCommand.ExecuteAsync(null);
        Assert.True(fake.Running);
        Assert.False(fake.Autostart);
    }

    [Fact]
    public async Task The_fake_method_checks_its_argument_like_the_core()
    {
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        Assert.True((await fake.CallAsync("autostart", new JsonObject { ["enable"] = "0" }))["ok"]!.GetValue<bool>());
        Assert.False(fake.Autostart);
        Assert.True(fake.Running);
        var bad = await fake.CallAsync("autostart", new JsonObject { ["enable"] = "yes" });
        Assert.Equal("bad_args", bad["error"]!.GetValue<string>());
        Assert.Equal("bad_args", (await fake.CallAsync("autostart"))["error"]!.GetValue<string>());
    }
}
