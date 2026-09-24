using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>
/// Status warning waiting_network: the engine runs with a network filter (Wi-Fi names / not in a corporate network)
/// and waits for such a network. The bypass is on and will work by itself: yellow "waiting", neither green nor red.
/// </summary>
public sealed class WaitingNetworkTests
{
    private const string Waiting = """
        {"ok":true,"enabled":true,"running":true,"warnings":["waiting_network"],"engine_stats":{"phase":"waiting_network"}}
        """;

    private const string Working = """{"ok":true,"enabled":true,"running":true,"warnings":[]}""";

    [Theory]
    [InlineData("ru", "Обход ждёт выбранную сеть")]
    [InlineData("en", "Waiting for the selected network")]
    [InlineData("zh-CN", "等待所选网络")]
    public void Warning_has_its_texts_in_every_language(string lang, string title)
    {
        L.SetLanguage(lang);
        try
        {
            var w = UiText.Warning("waiting_network");
            Assert.Equal(title, w.Title);
            Assert.Contains(L.T("Settings.Net"), w.Text, StringComparison.Ordinal);
            Assert.True(w.IsInfo);
            Assert.Equal("settings", w.Action);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Fact]
    public void Home_shows_waiting_in_yellow_not_working_or_broken()
    {
        L.SetLanguage("ru");
        var st = Make.Json(Waiting);
        var hero = HomeViewModel.Hero(st, true, true, null, false, UiText.IsWaitingNetwork(st));
        Assert.Equal(Kind.Warn, hero.Kind);
        Assert.Equal(L.T("Home.Hero.Waiting"), hero.Title);
        Assert.NotEqual(Kind.Fail, hero.Kind);
    }

    [Fact]
    public void Negative_control_a_working_bypass_is_still_green()
    {
        var st = Make.Json(Working);
        Assert.False(UiText.IsWaitingNetwork(st));
        Assert.Equal(Kind.Ok, HomeViewModel.Hero(st, true, true, null, false, UiText.IsWaitingNetwork(st)).Kind);
    }

    [Fact]
    public void The_phase_alone_is_enough()
    {
        Assert.True(UiText.IsWaitingNetwork(Make.Json("""{"running":true,"engine_stats":{"phase":"waiting_network"}}""")));
    }

    [Fact]
    public void Title_bar_and_tray_show_waiting_as_a_warning()
    {
        L.SetLanguage("ru");
        var state = new AppState(new ScriptedClient((_, _) => Make.Json("""{"ok":true}""")), new FakePlatform(), UiPrefs.Load(null));
        var shell = new ShellViewModel(state);
        state.Connection = ConnectionState.Available;
        state.Status = Make.Json(Waiting);
        shell.Update();
        Assert.Equal("warn", shell.TrayState);
        Assert.Equal(Kind.Warn, shell.PillKind);
        Assert.Equal(L.T("Shell.Pill.Waiting"), shell.PillText);

        state.Status = Make.Json(Working);
        shell.Update();
        Assert.Equal("on", shell.TrayState);
    }
}
