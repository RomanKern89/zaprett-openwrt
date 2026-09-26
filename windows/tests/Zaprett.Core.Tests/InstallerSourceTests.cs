using System.Text;
using System.Xml.Linq;

namespace Zaprett.Core.Tests;

/// <summary>
/// Installer sources (windows/installer): the desktop shortcut option and the UAC hints of 0.1.3 (ZERR-058) are
/// wired the same way in every language. The MSI itself is checked on test machines; this guards the sources.
/// </summary>
public class InstallerSourceTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Wxl = "http://wixtoolset.org/schemas/v4/wxl";
    private static readonly string[] NewStrings = { "ZaprettWarningDlgDesktop", "VerifyReadyDlgInstallText", "UserExitDescription1" };

    private static string InstallerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Zaprett.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "installer");
    }

    private static Dictionary<string, string> Strings(string culture)
    {
        var doc = XDocument.Load(Path.Combine(InstallerDir(), "Localization", culture + ".wxl"));
        return doc.Root!.Elements(Wxl + "String").ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Value")!);
    }

    [Theory]
    [InlineData("ru-RU", 1251)]
    [InlineData("en-US", 1252)]
    [InlineData("zh-CN", 936)]
    public void NewStrings_ExistInEveryLanguageAndFitItsCodePage(string culture, int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var strings = Strings(culture);
        foreach (string id in NewStrings)
        {
            Assert.True(strings.TryGetValue(id, out string? value), $"{culture}: {id} is missing");
            Assert.False(string.IsNullOrWhiteSpace(value));
            enc.GetBytes(value!); // throws when a character does not exist in the package code page
        }
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void UacHints_NameTheSetupExeAndTheTwoMinutes(string culture)
    {
        var s = Strings(culture);
        Assert.Contains("zaprett-setup.exe", s["UserExitDescription1"], StringComparison.Ordinal);
        Assert.Contains("[ProductName]", s["UserExitDescription1"], StringComparison.Ordinal);
        Assert.Contains("2", s["VerifyReadyDlgInstallText"], StringComparison.Ordinal);
        // the stock "click Finish to exit" sentence is appended to the same 220x80 text control and cut our text off
        Assert.Equal("", s["UserExitDescription2"]);
    }

    [Fact]
    public void DesktopShortcut_IsAConditionalComponentWithARememberedChoice()
    {
        var pkg = XDocument.Load(Path.Combine(InstallerDir(), "Package.wxs"));
        var component = pkg.Descendants(Wix + "Component").Single(c => (string?)c.Attribute("Id") == "UiDesktopShortcut");
        Assert.Equal("DesktopFolder", (string?)component.Attribute("Directory"));
        Assert.Equal("DESKTOPSHORTCUT = \"1\"", (string?)component.Attribute("Condition"));
        Assert.Equal("[#UiExe]", (string?)component.Element(Wix + "Shortcut")!.Attribute("Target"));

        var saved = pkg.Descendants(Wix + "Component").Single(c => (string?)c.Attribute("Id") == "InstallOptionDesktopShortcut");
        Assert.Null(saved.Attribute("Condition"));
        Assert.Equal("[DESKTOPSHORTCUT]", (string?)saved.Element(Wix + "RegistryValue")!.Attribute("Value"));

        var defaults = pkg.Descendants(Wix + "SetProperty").Where(p => (string?)p.Attribute("Id") == "DESKTOPSHORTCUT").ToList();
        Assert.Contains(defaults, p => (string?)p.Attribute("Value") == "1" && (string?)p.Attribute("Condition") == "NOT DESKTOPSHORTCUT");
        Assert.Contains(defaults, p => (string?)p.Attribute("Value") == "[ZAPRETT_SAVED_DESKTOPSHORTCUT]");
        Assert.Contains(pkg.Descendants(Wix + "Property"), p => (string?)p.Attribute("Id") == "DESKTOPSHORTCUT" && (string?)p.Attribute("Secure") == "yes");
    }

    [Fact]
    public void DesktopCheckbox_WritesOneOrZero()
    {
        var ui = XDocument.Load(Path.Combine(InstallerDir(), "UI.wxs"));
        var box = ui.Descendants(Wix + "Control").Single(c => (string?)c.Attribute("Id") == "DesktopCheckBox");
        Assert.Equal("ZAPRETT_DESKTOP_CHK", (string?)box.Attribute("Property"));
        Assert.Null(box.Attribute("HideCondition")); // shown on an upgrade too

        var next = ui.Descendants(Wix + "Publish")
            .Where(p => (string?)p.Attribute("Dialog") == "ZaprettWarningDlg" && (string?)p.Attribute("Control") == "Next").ToList();
        Assert.Contains(next, p => (string?)p.Attribute("Property") == "DESKTOPSHORTCUT" && (string?)p.Attribute("Value") == "1"
                                   && (string?)p.Attribute("Condition") == "ZAPRETT_DESKTOP_CHK");
        Assert.Contains(next, p => (string?)p.Attribute("Property") == "DESKTOPSHORTCUT" && (string?)p.Attribute("Value") == "0"
                                   && (string?)p.Attribute("Condition") == "NOT ZAPRETT_DESKTOP_CHK");
        int order(string? value) => int.Parse((string)next.Single(p => (string?)p.Attribute("Value") == value).Attribute("Order")!);
        Assert.True(order("1") < order("InstallDirDlg") && order("0") < order("InstallDirDlg"), "the choice is written before the next page");
    }
}
