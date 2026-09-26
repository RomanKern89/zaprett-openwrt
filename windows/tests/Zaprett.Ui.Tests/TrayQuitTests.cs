using System.Text.Json.Nodes;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;

namespace Zaprett.Ui.Tests;

/// <summary>"Close zaprett" of the tray menu: the bypass goes off, then the interface and its icon close.</summary>
public class TrayQuitTests
{
    [Theory]
    [InlineData(true, true, true, TrayQuitAction.StopThenExit)]
    [InlineData(true, false, true, TrayQuitAction.ExitOnly)]   // bypass already off: nothing to stop
    [InlineData(false, false, true, TrayQuitAction.ExitOnly)]  // no service: nothing to stop
    [InlineData(false, true, true, TrayQuitAction.ExitOnly)]   // stale "running" of an unavailable service
    [InlineData(true, true, false, TrayQuitAction.NotAllowed)] // view-only user cannot turn the bypass off
    [InlineData(true, false, false, TrayQuitAction.ExitOnly)]  // view-only, bypass off: closing is harmless
    public void Decide(bool available, bool running, bool canModify, TrayQuitAction expected)
    {
        Assert.Equal(expected, TrayQuit.Decide(available, running, canModify));
    }

    [Fact]
    public async Task Stop_then_the_bypass_is_off_in_the_service()
    {
        // what StopThenExit sends: "stop" turns the engine off in the service, and the next status says so
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        await fake.CallAsync("start");
        Assert.True((await fake.CallAsync("status"))["running"]!.GetValue<bool>());
        await fake.CallAsync("stop");
        Assert.False((await fake.CallAsync("status"))["running"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void The_two_exit_items_say_different_things(string lang)
    {
        L.SetLanguage(lang);
        try
        {
            var quit = L.T("Tray.Quit");
            var exit = L.T("Tray.Exit");
            Assert.False(string.IsNullOrWhiteSpace(quit));
            Assert.NotEqual("Tray.Quit", quit);
            Assert.NotEqual(exit, quit);
            Assert.Contains("zaprett", quit, StringComparison.Ordinal);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }
}
