using System.Text.Json.Nodes;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>repo.autoupdate: "Update strategies and lists from the repository daily" in the settings and the wizard.</summary>
public sealed class RepoAutoupdateTests
{
    private const string DefaultUrl = "https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json";

    [Fact]
    public void The_description_says_where_the_program_goes()
    {
        L.SetLanguage("en");
        Assert.Equal("GitHub, repository CherretGit/zaprett-repo", RepoSource.Where(DefaultUrl));
        Assert.Equal("GitHub, repository someone/other", RepoSource.Where("https://github.com/someone/other/raw/main/index.json"));
        Assert.Equal("mirror.example.org", RepoSource.Where("https://Mirror.example.org/zaprett/index.json"));
        Assert.Null(RepoSource.Where(null));
        Assert.Null(RepoSource.Where("not a url"));
        Assert.Contains("GitHub, repository CherretGit/zaprett-repo", RepoSource.Hint(null), StringComparison.Ordinal);
        Assert.Contains("Once a day", RepoSource.Hint(Make.Json($$$"""{"repo":{"url":"{{{DefaultUrl}}}"}}""")), StringComparison.Ordinal);
    }

    /// <summary>A complete config.json (the fake's), so only the repository setting can make the page dirty.</summary>
    private static async Task<(SettingsViewModel Vm, JsonObject Config)> WithConfig()
    {
        var (state, fake, _) = Make.State();
        var config = (await fake.CallAsync("settings.get")).Obj("settings")!;
        return (new SettingsViewModel(state, null), config);
    }

    [Fact]
    public async Task The_switch_follows_config_and_a_missing_value_means_on()
    {
        var (vm, config) = await WithConfig();
        config["repo"]!["autoupdate"] = false;
        vm.Load(config);
        Assert.False(vm.RepoAutoupdate);
        Assert.Empty(vm.BuildPatch());
        config.Remove("repo");
        vm.Load(config);
        Assert.True(vm.RepoAutoupdate);
        Assert.False(vm.IsDirty);
        Assert.Empty(vm.BuildPatch());
    }

    [Fact]
    public async Task Turning_it_off_sends_only_repo_autoupdate()
    {
        var (vm, config) = await WithConfig();
        vm.Load(config);
        Assert.Empty(vm.BuildPatch());
        vm.RepoAutoupdate = false;
        Assert.True(vm.IsDirty);
        Assert.Equal("""{"repo":{"autoupdate":false}}""", vm.BuildPatch().ToJsonString());
        vm.RepoAutoupdate = true;
        Assert.False(vm.IsDirty);
    }

    [Theory]
    [InlineData("SettingsPage.xaml", typeof(SettingsViewModel))]
    [InlineData("WizardPage.xaml", typeof(WizardViewModel))]
    [InlineData("DiagnosticsPage.xaml", typeof(DiagnosticsViewModel))]
    public void Every_switch_starts_off_and_gets_its_value_after_the_page_is_shown(string page, Type vmType)
    {
        // a ToggleSwitch created already on keeps the pale colours of the middle of its animation (light-en-20)
        var xaml = File.ReadAllText(Path.Combine(Repo.Root, "src", "Zaprett.Ui", "Views", page));
        var names = System.Text.RegularExpressions.Regex.Matches(xaml, @"IsOn=""\{x:Bind Vm\.(\w+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(names);
        var (state, _, _) = Make.State();
        var vm = Activator.CreateInstance(vmType, state, null)!;
        foreach (var name in names)
            Assert.False((bool)vmType.GetProperty(name)!.GetValue(vm)!, $"{page}: {name} is on before the page loaded");
    }

    [Fact]
    public async Task Saved_with_the_fake_service_keeps_the_address_and_the_hour()
    {
        var (state, fake, _) = Make.State();
        await state.RefreshAsync();
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        Assert.True(vm.RepoAutoupdate);
        vm.RepoAutoupdate = false;
        await vm.SaveCommand.ExecuteAsync(null);
        var repo = (await fake.CallAsync("settings.get")).Obj("settings").Obj("repo");
        Assert.False(repo.Bool("autoupdate", true));
        Assert.Equal(DefaultUrl, repo.Str("url"));
        Assert.Equal(4, repo.Int("autoupdate_hour"));
        Assert.False(vm.RepoAutoupdate);
        Assert.False(vm.IsDirty);
    }

    private static async Task<(WizardViewModel Vm, FakeZaprettClient Fake)> AtDone()
    {
        L.SetLanguage("en");
        var (state, fake, _) = Make.State(FakeZaprettClient.Scenarios.FirstRun);
        await state.RefreshAsync();
        var vm = new WizardViewModel(state, null);
        for (var i = 0; i < 4 && vm.Step != WizardStep.Done; i++)
            await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Done, vm.Step);
        return (vm, fake);
    }

    [Fact]
    public async Task The_wizard_offers_it_on_and_saves_a_change()
    {
        var (vm, fake) = await AtDone();
        Assert.True(vm.IsRepoAutoupdate);
        Assert.Contains("CherretGit/zaprett-repo", vm.RepoAutoupdateHint, StringComparison.Ordinal);
        vm.IsRepoAutoupdate = false;
        await vm.FinishCommand.ExecuteAsync(null);
        Assert.False(vm.HasMessage);
        Assert.False((await fake.CallAsync("settings.get")).Obj("settings").Obj("repo").Bool("autoupdate", true));
    }

    [Fact]
    public async Task The_wizard_sends_nothing_when_it_was_left_as_is()
    {
        var (vm, fake) = await AtDone();
        var before = fake.Calls.Count(c => c == "settings.set");
        await vm.FinishCommand.ExecuteAsync(null);
        Assert.Equal(before, fake.Calls.Count(c => c == "settings.set"));
        Assert.True((await fake.CallAsync("settings.get")).Obj("settings").Obj("repo").Bool("autoupdate", false));
    }
}
