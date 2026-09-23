using System.Text.Json.Nodes;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>Beta 0.1.0: the service cannot update the program (UpdateControlStub → not_supported); the settings then
/// show how to update by hand instead of a channel and a "Check now" that do nothing.</summary>
public sealed class UpdateSettingsTests
{
    private static JsonObject Answer(string method, string? updateCheck) => method switch
    {
        "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{},"update":{"channel":"stable","check":true}}}"""),
        "update.check" when updateCheck != null => Make.Json(updateCheck),
        _ => Make.Json("""{"ok":true}"""),
    };

    private static async Task<(SettingsViewModel Vm, ScriptedClient Client, AppState State)> Load(string? updateCheck, AppState? shared = null)
    {
        L.SetLanguage("ru");
        var client = new ScriptedClient((m, _) => Answer(m, updateCheck));
        var state = shared ?? new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        return (vm, client, state);
    }

    [Fact]
    public async Task Not_supported_hides_channel_and_check_and_shows_the_releases_page()
    {
        var (vm, _, _) = await Load("""{"ok":false,"error":"not_supported","message":"Program updates are not available in this build"}""");
        Assert.False(vm.UpdatesSupported);
        Assert.False(vm.ShowUpdateControls);
        Assert.True(vm.ShowUpdatesUnavailable);
        Assert.Equal("https://github.com/RomanKern89/zaprett-openwrt/releases", vm.ReleasesUri.AbsoluteUri);
        Assert.Contains("скачайте новый установщик", L.T("Settings.Update.ManualText"), StringComparison.Ordinal);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public async Task Working_updates_keep_the_controls()
    {
        var (vm, _, _) = await Load("""{"ok":true,"current":"0.2.0","latest":"0.3.0","update_available":true}""");
        Assert.True(vm.UpdatesSupported);
        Assert.True(vm.ShowUpdateControls);
        Assert.False(vm.ShowUpdatesUnavailable);
        Assert.True(vm.UpdateAvailable);
        Assert.Equal(L.F("Settings.Update.Available", "0.3.0"), vm.UpdateText);
    }

    [Fact]
    public async Task A_failed_check_is_not_taken_for_missing_updates()
    {
        var (vm, _, _) = await Load("""{"ok":false,"error":"download_failed","message":"no network"}""");
        Assert.True(vm.ShowUpdateControls);
        Assert.False(vm.ShowUpdatesUnavailable);
    }

    [Fact]
    public async Task The_service_is_asked_once_per_session()
    {
        var (_, client, state) = await Load("""{"ok":false,"error":"not_supported"}""");
        Assert.Single(client.Calls, c => c.Method == "update.check");
        var second = new SettingsViewModel(state, null);
        await second.LoadAsync();
        Assert.True(second.ShowUpdatesUnavailable);
        Assert.Single(client.Calls, c => c.Method == "update.check");
    }

    [Fact]
    public async Task The_fake_behaves_like_the_service_of_0_1_0()
    {
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        var r = await fake.CallAsync("update.check", new JsonObject { ["lang"] = "ru" });
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("not_supported", r["error"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_page_binds_the_update_cards_to_the_answer_of_the_service()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Zaprett.slnx")))
            dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Zaprett.Ui", "Views", "SettingsPage.xaml"));
        var channel = xaml.IndexOf("Settings.Channel')", StringComparison.Ordinal);
        var check = xaml.IndexOf("Settings.CheckUpdates')", StringComparison.Ordinal);
        var manual = xaml.IndexOf("Settings.Update.ManualTitle')", StringComparison.Ordinal);
        Assert.True(channel > 0 && check > 0 && manual > 0);
        string Card(int at) => xaml[xaml.LastIndexOf("<ui:SettingCard", at, StringComparison.Ordinal)..xaml.IndexOf('>', at)];
        Assert.Contains("Vm.ShowUpdateControls", Card(channel), StringComparison.Ordinal);
        Assert.Contains("Vm.ShowUpdateControls", Card(check), StringComparison.Ordinal);
        Assert.Contains("Vm.ShowUpdatesUnavailable", Card(manual), StringComparison.Ordinal);
        Assert.Contains("NavigateUri=\"{x:Bind Vm.ReleasesUri}\"", xaml, StringComparison.Ordinal);
    }
}
