using Zaprett.Ui.Core.Services;

namespace Zaprett.Ui.Tests;

public sealed class WizardDecisionTests
{
    [Theory]
    // clean install without parameters (Windows 10 test machine, 2026-09-23): default lists written, bypass not turned on
    [InlineData("""{"enabled":false,"running":false,"lists":["zaprett-youtube","zaprett-discord","user-hosts"],"ipsets":[]}""", true)]
    // default IP-network list only, bypass off
    [InlineData("""{"enabled":false,"running":false,"lists":[],"ipsets":["zaprett-discord-voice"]}""", true)]
    // nothing at all
    [InlineData("""{"enabled":false,"running":false,"lists":[],"ipsets":[]}""", true)]
    // fields missing in an older service: nothing known to be turned on
    [InlineData("""{"enabled":false}""", true)]
    // MSI with SERVICES and AUTOSTART=1 (Windows 11 test machine): bypass on
    [InlineData("""{"enabled":true,"running":true,"lists":["zaprett-youtube"],"ipsets":[]}""", false)]
    // bypass on, engine not (yet) running
    [InlineData("""{"enabled":true,"running":false,"lists":[],"ipsets":[]}""", false)]
    // engine running although the switch reads off (turned on from the command line a moment ago)
    [InlineData("""{"enabled":false,"running":true,"lists":["zaprett-youtube"],"ipsets":[]}""", false)]
    public void Wizard_opens_until_the_bypass_has_been_turned_on(string status, bool expected)
    {
        Assert.Equal(expected, WizardDecision.ShouldOpen(false, Make.Json(status)));
    }

    [Fact]
    public void Wizard_done_never_opens_it_again()
    {
        Assert.False(WizardDecision.ShouldOpen(true, Make.Json("""{"enabled":false,"lists":[],"ipsets":[]}""")));
        // a clean install with the wizard already passed (or skipped) goes home too
        Assert.False(WizardDecision.ShouldOpen(true, Make.Json("""{"enabled":false,"running":false,"lists":["zaprett-youtube"],"ipsets":[]}""")));
        Assert.False(WizardDecision.ShouldOpen(true, null));
    }

    [Fact]
    public void Without_a_status_the_decision_waits()
    {
        Assert.Null(WizardDecision.ShouldOpen(false, null));
    }
}

public sealed class WindowPlacementTests
{
    private static readonly PixelRect Small = new(0, 0, 1280, 752); // 1280×800 minus the taskbar
    private static readonly PixelRect Big = new(0, 0, 2560, 1400);

    private static bool Inside(PixelRect r, PixelRect area) =>
        r.X >= area.X && r.Y >= area.Y && r.X + r.Width <= area.X + area.Width && r.Y + r.Height <= area.Y + area.Height;

    [Fact]
    public void Small_screen_the_window_fits_the_work_area()
    {
        var r = WindowPlacement.Compute(Small, 1.0, null, [Small]);
        Assert.True(Inside(r, Small), r.ToString());
        Assert.Equal(1280, r.Width);
        Assert.Equal(752, r.Height);
    }

    [Fact]
    public void Big_screen_the_window_has_the_preferred_size_scaled_and_centred()
    {
        var r = WindowPlacement.Compute(Big, 1.5, null, [Big]);
        Assert.Equal(new PixelRect((2560 - 1920) / 2, (1400 - 1290) / 2, 1920, 1290), r);
    }

    [Fact]
    public void Screen_narrower_than_the_preferred_width_limits_the_width()
    {
        // 1152×864 and 1024×768: both sides smaller than 1280×860
        foreach (var area in new[] { new PixelRect(0, 0, 1152, 824), new PixelRect(0, 0, 1024, 728) })
        {
            var r = WindowPlacement.Compute(area, 1.0, null, [area]);
            Assert.Equal(area, r);
        }
        // 125 % on 1366×768: 1600 px wanted, 1366 available
        var laptop = new PixelRect(0, 0, 1366, 720);
        var s = WindowPlacement.Compute(laptop, 1.25, null, [laptop]);
        Assert.True(Inside(s, laptop), s.ToString());
    }

    [Fact]
    public void Scaled_small_screen_still_fits()
    {
        // 1920×1080 at 150 %: the preferred 1280×860 DIP would be 1920×1290 px
        var area = new PixelRect(0, 0, 1920, 1032);
        var r = WindowPlacement.Compute(area, 1.5, null, [area]);
        Assert.True(Inside(r, area), r.ToString());
    }

    [Fact]
    public void Saved_position_on_a_current_monitor_is_reused()
    {
        var saved = new PixelRect(100, 80, 1100, 700);
        Assert.Equal(saved, WindowPlacement.Compute(Big, 1.0, saved, [Big]));
    }

    [Fact]
    public void Saved_position_on_a_missing_monitor_is_dropped()
    {
        // the second monitor at x = 2560 is gone
        var saved = new PixelRect(2700, 100, 1200, 800);
        var r = WindowPlacement.Compute(Small, 1.0, saved, [Small]);
        Assert.True(Inside(r, Small), r.ToString());
        Assert.NotEqual(saved.X, r.X);
    }

    [Fact]
    public void Saved_window_bigger_than_the_screen_is_pulled_inside()
    {
        // saved on a big monitor, now the laptop screen only; the centre is still on it
        var saved = new PixelRect(200, 100, 1600, 1000);
        var r = WindowPlacement.Compute(Small, 1.0, saved, [Small]);
        Assert.True(Inside(r, Small), r.ToString());
    }

    [Fact]
    public void Second_monitor_with_negative_coordinates_keeps_the_saved_position()
    {
        var left = new PixelRect(-1920, 0, 1920, 1040);
        var saved = new PixelRect(-1500, 100, 1200, 800);
        Assert.Equal(saved, WindowPlacement.Compute(Big, 1.0, saved, [Big, left]));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Broken_scale_counts_as_100_percent(double scale)
    {
        Assert.Equal(WindowPlacement.Compute(Big, 1.0, null, [Big]), WindowPlacement.Compute(Big, scale, null, [Big]));
    }

    [Fact]
    public void Minimum_size_never_exceeds_the_work_area()
    {
        var tiny = new PixelRect(0, 0, 700, 500);
        Assert.Equal((700, 500), WindowPlacement.MinimumSize(tiny, 1.0));
        Assert.Equal((760, 560), WindowPlacement.MinimumSize(Big, 1.0));
    }

    [Fact]
    public void Window_placement_survives_the_prefs_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zaprett-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ui.json");
            var prefs = UiPrefs.Load(path);
            Assert.Null(prefs.Window);
            prefs.Window = SavedWindow.From(new PixelRect(-100, 20, 1100, 720));
            prefs.Save();
            Assert.Equal(new PixelRect(-100, 20, 1100, 720), UiPrefs.Load(path).Window?.ToRect());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
