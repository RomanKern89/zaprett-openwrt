using System.Globalization;
using Zaprett.Setup;

namespace Zaprett.Core.Tests;

/// <summary>zaprett-setup.exe (windows/setup/SetupLogic.cs, linked into this project): arguments and messages.</summary>
public class SetupLogicTests
{
    [Theory]
    [InlineData("\"C:\\Users\\a b\\Downloads\\zaprett-0.1.3-x64-setup.exe\"", "")]
    [InlineData("\"C:\\Users\\a b\\zaprett-setup.exe\"  /qn SERVICES=youtube", "/qn SERVICES=youtube")]
    [InlineData("zaprett-setup.exe /l*v setup.log", "/l*v setup.log")]
    [InlineData("C:\\t\\zaprett-setup.exe INSTALLFOLDER=\"D:\\Apps\\zap rett\" /qb", "INSTALLFOLDER=\"D:\\Apps\\zap rett\" /qb")]
    [InlineData("", "")]
    public void RawArguments_KeepsTheUsersQuotingAfterTheProgramName(string commandLine, string expected)
    {
        Assert.Equal(expected, SetupLogic.RawArguments(commandLine));
    }

    [Fact]
    public void RawArguments_UnclosedQuoteMeansNoArguments()
    {
        Assert.Equal("", SetupLogic.RawArguments("\"D:\\zaprett-setup.exe /qn"));
    }

    [Theory]
    [InlineData("--zaprett-elevated", true, "")]
    [InlineData("--zaprett-elevated /qn LANG=en", true, "/qn LANG=en")]
    [InlineData("/qn --zaprett-elevated", false, "/qn --zaprett-elevated")]
    [InlineData("--zaprett-elevatedX", false, "--zaprett-elevatedX")]
    [InlineData("", false, "")]
    public void ElevatedMarker_IsOnlyTheWholeFirstToken(string raw, bool stripped, string rest)
    {
        Assert.Equal(stripped, SetupLogic.TryStripElevatedMarker(raw, out string actual));
        Assert.Equal(rest, actual);
    }

    [Fact]
    public void MsiexecArguments_InstallsTheQuotedPackageThenTheUsersArguments()
    {
        Assert.Equal("/i \"C:\\T\\z s\\zaprett-0.1.3-x64.msi\"", SetupLogic.MsiexecArguments("C:\\T\\z s\\zaprett-0.1.3-x64.msi", ""));
        Assert.Equal("/i \"C:\\p.msi\" /qn AUTOSTART=1", SetupLogic.MsiexecArguments("C:\\p.msi", "  /qn AUTOSTART=1 "));
    }

    [Theory]
    [InlineData("/qn", true)]
    [InlineData("/QN", true)]
    [InlineData("/q", true)]
    [InlineData("/qb-", true)]
    [InlineData("/qb!", true)]
    [InlineData("/qr", true)]
    [InlineData("-qn", true)]
    [InlineData("/passive", true)]
    [InlineData("/quiet", true)]
    [InlineData("SERVICES=youtube /qn", true)]
    [InlineData("/qf", false)]
    [InlineData("", false)]
    [InlineData("/l*v setup.log", false)]
    [InlineData("QUIET=1", false)]
    [InlineData("INSTALLFOLDER=D:\\qn", false)]
    public void IsQuiet_OnlyForTheQuietAndBasicUiSwitches(string raw, bool quiet)
    {
        Assert.Equal(quiet, SetupLogic.IsQuiet(raw));
    }

    // a switch inside a quoted property value is data, not a switch (the bootstrapper would hide its own errors)
    [Theory]
    [InlineData("PROP=\"a /qn b\"", false)]
    [InlineData("PROP=\"a /qn b\" /qb", true)]
    [InlineData("INSTALLFOLDER=\"D:\\x /quiet\"", false)]
    public void IsQuiet_IgnoresSwitchesInsideQuotes(string raw, bool quiet)
    {
        Assert.Equal(quiet, SetupLogic.IsQuiet(raw));
    }

    [Fact]
    public void Tokens_SplitsOnlyOutsideQuotes()
    {
        Assert.Equal(new[] { "/qn", "PROP=\"a b\"", "/l*v", "x.log" }, SetupLogic.Tokens("/qn  PROP=\"a b\"\t/l*v x.log "));
        Assert.Empty(SetupLogic.Tokens(""));
        Assert.Empty(SetupLogic.Tokens(null!));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    [InlineData(1602, false)]
    [InlineData(1603, false)]
    [InlineData(1619, false)]
    public void IsSuccess_CountsRestartRequiredAsInstalled(int code, bool ok)
    {
        Assert.Equal(ok, SetupLogic.IsSuccess(code));
    }

    [Fact]
    public void NotElevatedDetail_ExistsInEveryLanguage()
    {
        foreach (string lang in new[] { "ru", "en", "zh" })
            Assert.False(string.IsNullOrWhiteSpace(SetupLogic.NotElevatedDetail(lang)));
        Assert.NotEqual(SetupLogic.NotElevatedDetail("ru"), SetupLogic.NotElevatedDetail("en"));
    }

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("ru", "ru")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "zh")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("uk-UA", "en")]
    public void LanguageOf_FollowsTheInterfaceLanguage(string culture, string lang)
    {
        Assert.Equal(lang, SetupLogic.LanguageOf(new CultureInfo(culture)));
    }

    // the retry button is named as Windows labels it in the message box (MB_RETRYCANCEL): ru «Повтор», en Retry, zh 重试
    [Theory]
    [InlineData("ru", "«Да»", "«Повтор»")]
    [InlineData("en", "Yes", "Retry")]
    [InlineData("zh", "“是”", "“重试”")]
    public void ElevationDeclinedText_SaysWhatToPress(string lang, string yes, string retry)
    {
        string text = SetupLogic.ElevationDeclinedText(lang);
        Assert.Contains(yes, text, StringComparison.Ordinal);
        Assert.Contains(retry, text, StringComparison.Ordinal);
        // a postponed UAC prompt of the exe flashes as the zaprett icon on the taskbar, not as a shield
        Assert.DoesNotContain("щита", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shield", text, StringComparison.Ordinal);
        Assert.DoesNotContain("盾牌", text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(SetupLogic.Title(lang)));
    }

    [Fact]
    public void PrepareFailedText_CarriesTheDetail()
    {
        foreach (string lang in new[] { "ru", "en", "zh" })
            Assert.Contains("disk full", SetupLogic.PrepareFailedText(lang, "disk full"), StringComparison.Ordinal);
    }
}
