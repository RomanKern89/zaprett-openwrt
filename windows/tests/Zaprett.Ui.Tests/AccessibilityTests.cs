using System.Text.RegularExpressions;

namespace Zaprett.Ui.Tests;

/// <summary>
/// Narrator and UI automation read a control by its name. A button whose content is a panel (icon + text, progress
/// ring + text) has no name of its own, so every interactive control in XAML needs a plain-text Content, a Header,
/// AutomationProperties.Name/LabeledBy, or has to be the control of a SettingCard (which names it after its title).
/// </summary>
public sealed partial class AccessibilityTests
{
    [GeneratedRegex("""<(Button|ToggleButton|HyperlinkButton|DropDownButton|SplitButton|RepeatButton|ToggleSplitButton|ToggleSwitch|CheckBox|RadioButton|ComboBox|NumberBox|TextBox|PasswordBox|Slider)(\s[^>]*?)?/?>""", RegexOptions.Singleline)]
    private static partial Regex Control();

    [GeneratedRegex("""<ui:SettingCard\s[^>]*>\s*$""", RegexOptions.Singleline)]
    private static partial Regex SettingCardOpenAtEnd();

    [GeneratedRegex("""\sContent="(?!\{x:Bind\s+[A-Za-z.]*Icon)""")]
    private static partial Regex TextContent();

    /// <summary>Controls without an accessible name in one XAML text, as "line: tag".</summary>
    public static List<string> Unnamed(string xaml)
    {
        var result = new List<string>();
        foreach (Match m in Control().Matches(xaml))
        {
            var attrs = m.Groups[2].Value;
            if (attrs.Contains("AutomationProperties.Name=", StringComparison.Ordinal)
                || attrs.Contains("AutomationProperties.LabeledBy=", StringComparison.Ordinal)
                || attrs.Contains("Header=", StringComparison.Ordinal)
                || TextContent().IsMatch(attrs))
                continue;
            if (SettingCardOpenAtEnd().IsMatch(xaml[..m.Index]))
                continue;
            var line = xaml[..m.Index].Count(c => c == '\n') + 1;
            result.Add($"{line}: <{m.Groups[1].Value}");
        }
        return result;
    }

    private static IEnumerable<string> XamlFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Zaprett.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var ui = Path.Combine(dir!.FullName, "src", "Zaprett.Ui");
        return Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_interactive_control_has_an_accessible_name()
    {
        var files = XamlFiles().ToList();
        Assert.True(files.Count >= 10, "too few XAML files found: " + files.Count);
        var bad = files.SelectMany(f => Unnamed(File.ReadAllText(f)).Select(u => Path.GetFileName(f) + ":" + u)).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Negative_control_a_button_with_panel_content_and_no_name_is_found()
    {
        var xaml = """
            <StackPanel>
                <Button Content="{x:Bind loc:L.T('Common.Copy')}" />
                <Button Command="{x:Bind Vm.NextCommand}">
                    <StackPanel><ProgressRing /><TextBlock Text="{x:Bind Vm.NextLabel}" /></StackPanel>
                </Button>
                <Button AutomationProperties.Name="{x:Bind Vm.NextLabel}" Command="{x:Bind Vm.NextCommand}">
                    <TextBlock Text="x" />
                </Button>
                <ui:SettingCard Header="{x:Bind loc:L.T('Settings.Ipv6')}">
                    <ToggleSwitch IsOn="{x:Bind Vm.Ipv6, Mode=TwoWay}" />
                </ui:SettingCard>
                <ToggleSwitch IsOn="{x:Bind Vm.Other, Mode=TwoWay}" />
            </StackPanel>
            """;
        Assert.Equal(["3: <Button", "12: <ToggleSwitch"], Unnamed(xaml));
    }
}
