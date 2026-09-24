using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

/// <summary>status.can_modify: a user who may only read gets every change locked in advance and sends none.</summary>
public sealed class AccessTests
{
    private const string ReadOnlyStatus = """{"ok":true,"enabled":true,"running":true,"can_modify":false,"tray_autostart":true}""";

    private static (AppState State, ScriptedClient Client) StateWith(string status, Func<string, JsonObject?, JsonObject?>? answer = null)
    {
        var client = new ScriptedClient((m, a) => answer?.Invoke(m, a) ?? m switch
        {
            "settings.get" => Make.Json("""{"ok":true,"settings":{"main":{},"ui":{"language":"ru"}}}"""),
            "update.check" => Make.Json("""{"ok":false,"error":"not_supported"}"""),
            _ => Make.Json("""{"ok":true}"""),
        });
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null)) { Status = Make.Json(status) };
        return (state, client);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("""{"running":true}""", true)]
    [InlineData("""{"can_modify":true}""", true)]
    [InlineData("""{"can_modify":false}""", false)]
    [InlineData("""{"can_modify":"false"}""", true)]
    [InlineData("""{"can_modify":null}""", true)]
    public void Only_an_explicit_false_locks(string? status, bool canModify)
    {
        Assert.Equal(canModify, ServiceAccess.CanModify(status == null ? null : Make.Json(status)));
    }

    [Fact]
    public void The_read_only_methods_are_the_same_as_in_the_core()
    {
        var source = File.ReadAllText(Path.Combine(Repo.Root, "src", "Zaprett.Core", "CommandDispatcher.cs"));
        var block = Regex.Match(source, @"HashSet<string> ReadOnly = new\(StringComparer\.Ordinal\)\s*\{(?<list>[^}]*)\}").Groups["list"].Value;
        var core = Regex.Matches(block, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(core);
        Assert.True(core.SetEquals(ServiceAccess.ReadOnlyMethods), "core: " + string.Join(",", core.Order()) + " ui: " + string.Join(",", ServiceAccess.ReadOnlyMethods.Order()));
        Assert.True(ServiceAccess.Modifies("tray.autostart"));
        Assert.False(ServiceAccess.Modifies("status"));
    }

    [Fact]
    public async Task A_change_is_not_sent_without_the_rights_and_reading_still_works()
    {
        var (state, client) = StateWith(ReadOnlyStatus);
        var e = await Assert.ThrowsAsync<ZaprettCallException>(() => state.CallAsync("start"));
        Assert.Equal("access_denied", e.Code);
        await state.CallAsync("status");
        Assert.DoesNotContain(client.Calls, c => c.Method == "start");
        Assert.Contains(client.Calls, c => c.Method == "status");
    }

    [Fact]
    public async Task A_service_without_the_field_gets_the_change_as_before()
    {
        var (state, client) = StateWith("""{"ok":true,"enabled":true,"running":true}""");
        await state.CallAsync("start");
        Assert.Contains(client.Calls, c => c.Method == "start");
    }

    [Fact]
    public async Task Pages_lock_their_changes_and_the_shell_says_why()
    {
        L.SetLanguage("ru");
        var (state, _) = StateWith(ReadOnlyStatus);
        state.Connection = ConnectionState.Available;
        var shell = new ShellViewModel(state);
        Assert.True(shell.IsReadOnly);
        Assert.False(shell.CanModify);
        Assert.False(new HomeViewModel(state, null).CanModify);

        var settings = new SettingsViewModel(state, null);
        await settings.LoadAsync();
        Assert.False(settings.CanChangeTrayAutostart);
        Assert.Equal(L.T("Settings.TrayAutostartDenied"), settings.TrayAutostartHint);

        var lists = new ListsViewModel(state, null);
        lists.Fill(Make.Json("""{"ok":true,"items":[{"id":"zaprett-youtube","type":"list","source":"preset","active":true}]}"""),
            Make.Json("""{"ok":true,"sources":[{"name":"s1","url":"https://example.org/l.txt","enabled":true}]}"""));
        Assert.False(Assert.Single(lists.Domains).CanToggle);
        Assert.False(Assert.Single(lists.Sources).CanModify);
    }

    [Fact]
    public async Task With_the_rights_everything_stays_open()
    {
        var (state, _) = StateWith("""{"ok":true,"enabled":true,"running":true,"can_modify":true,"tray_autostart":true}""");
        state.Connection = ConnectionState.Available;
        Assert.False(new ShellViewModel(state).IsReadOnly);
        var settings = new SettingsViewModel(state, null);
        await settings.LoadAsync();
        Assert.True(settings.CanChangeTrayAutostart);
        var lists = new ListsViewModel(state, null);
        lists.Fill(Make.Json("""{"ok":true,"items":[{"id":"zaprett-youtube","type":"list","source":"preset","active":true}]}"""), null);
        Assert.True(Assert.Single(lists.Domains).CanToggle);
    }

    [Fact]
    public async Task Losing_the_rights_is_announced_to_the_bindings()
    {
        var status = """{"ok":true,"enabled":true,"running":true,"can_modify":true,"tray_autostart":true}""";
        var (state, _) = StateWith(status, (m, _) => m == "page" ? Make.Json("{\"ok\":true,\"status\":" + status + "}") : null);
        var settings = new SettingsViewModel(state, null);
        await settings.LoadAsync();
        Assert.True(settings.CanChangeTrayAutostart);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        status = ReadOnlyStatus;
        await state.RefreshAsync();
        Assert.Contains(nameof(PageViewModel.CanModify), changed);
        Assert.Contains(nameof(SettingsViewModel.CanChangeTrayAutostart), changed);
        Assert.False(settings.CanChangeTrayAutostart);
    }

    [Fact]
    public async Task The_language_of_a_user_without_rights_is_their_own()
    {
        L.SetLanguage("ru");
        var (state, client) = StateWith(ReadOnlyStatus);
        var vm = new SettingsViewModel(state, null);
        await vm.LoadAsync();
        try
        {
            vm.Language = vm.Languages.Single(l => l.Value == "en");
            for (var i = 0; i < 100 && L.Language != "en"; i++)
                await Task.Delay(10);
            Assert.Equal("en", L.Language);
            Assert.True(state.Prefs.LanguagePersonal);
            Assert.DoesNotContain(client.Calls, c => c.Method == "settings.set");
            Assert.False(vm.HasMessage);
            // the shared language (ru) does not take it back at the next start
            await state.SyncLanguageAsync();
            Assert.Equal("en", L.Language);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Fact]
    public void The_wizard_is_not_offered_to_a_user_who_may_only_read()
    {
        var firstRun = """{"enabled":false,"running":false,"install_id":"7c1e2d9a-0f3b-4d61-9a55-000000000001"}""";
        Assert.True(WizardDecision.ShouldOpen(false, null, Make.Json(firstRun)));
        Assert.False(WizardDecision.ShouldOpen(false, null, Make.Json(firstRun.Replace("\"running\":false", "\"running\":false,\"can_modify\":false", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task The_fake_read_only_scenario_answers_like_the_service()
    {
        await using var fake = new FakeZaprettClient(FakeZaprettClient.Scenarios.ReadOnly) { JobSeconds = 0.01 };
        Assert.False((await fake.CallAsync("status"))["can_modify"]!.GetValue<bool>());
        var r = await fake.CallAsync("stop");
        Assert.Equal("access_denied", r["error"]!.GetValue<string>());
        Assert.True(fake.Running);
    }
}

/// <summary>
/// Every control that sends a change to the service binds IsEnabled to CanModify (directly or combined). The list
/// names the controls by a unique piece of their tag; a control added later without the binding is found by the
/// second test (every Command of a changing view-model method must be here).
/// </summary>
public sealed class ReadOnlyBindingTests
{
    public static readonly TheoryData<string, string> Controls = new()
    {
        { "HomePage.xaml", "Command=\"{x:Bind Vm.ToggleCommand}\"" },
        { "HomePage.xaml", "Command=\"{x:Bind Vm.CheckNowCommand}\"" },
        { "ServicesPage.xaml", "Command=\"{x:Bind Vm.ApplyCommand}\"" },
        { "StrategiesPage.xaml", "Command=\"{x:Bind Vm.StartTestCommand}\"" },
        { "StrategiesPage.xaml", "Command=\"{x:Bind Vm.StopTestCommand}\"" },
        { "StrategiesPage.xaml", "Command=\"{x:Bind Vm.ApplyBestCommand}\"" },
        { "StrategiesPage.xaml", "Command=\"{x:Bind Vm.SaveAndCheckCommand}\"" },
        { "StrategiesPage.xaml", "Command=\"{x:Bind Vm.SelectCommand}\"" },
        { "StrategiesPage.xaml", "Command=\"{x:Bind Vm.DeleteCommand}\"" },
        { "ListsPage.xaml", "Checked=\"OnWhitelist\"" },
        { "ListsPage.xaml", "Checked=\"OnBlacklist\"" },
        { "ListsPage.xaml", "Command=\"{x:Bind Vm.SaveUserCommand}\"" },
        { "ListsPage.xaml", "Command=\"{x:Bind Vm.UpdateSourcesCommand}\"" },
        { "ListsPage.xaml", "Command=\"{x:Bind Vm.AddSourceCommand}\"" },
        { "ListsPage.xaml", "IsOn=\"{x:Bind IsEnabled, Mode=TwoWay}\"" },
        { "ListsPage.xaml", "IsOn=\"{x:Bind IsActive, Mode=TwoWay}\"" },
        { "ListsPage.xaml", "Click=\"OnDeleteSource\"" },
        { "DiagnosticsPage.xaml", "Command=\"{x:Bind Vm.DiagnoseCommand}\"" },
        { "DiagnosticsPage.xaml", "Command=\"{x:Bind Vm.TurnOnDnsCommand}\"" },
        { "DiagnosticsPage.xaml", "IsOn=\"{x:Bind Vm.IsDebug, Mode=TwoWay}\"" },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.Autostart," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.TrayAutostart," },
        { "SettingsPage.xaml", "SelectedItem=\"{x:Bind Vm.Engine," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.Watchdog," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.Ipv6," },
        { "SettingsPage.xaml", "SelectedItem=\"{x:Bind Vm.DnsMode," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.QuicBlock," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.GameFilter," },
        { "SettingsPage.xaml", "Text=\"{x:Bind Vm.GamePortsTcp," },
        { "SettingsPage.xaml", "Text=\"{x:Bind Vm.GamePortsUdp," },
        { "SettingsPage.xaml", "SelectedItem=\"{x:Bind Vm.NetworkMode," },
        { "SettingsPage.xaml", "Text=\"{x:Bind Vm.Ssids," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.SkipCorporate," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.MonitorEnabled," },
        { "SettingsPage.xaml", "SelectedItem=\"{x:Bind Vm.MonitorInterval," },
        { "SettingsPage.xaml", "Value=\"{x:Bind Vm.MonitorThreshold," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.AutoRepair," },
        { "SettingsPage.xaml", "SelectedItem=\"{x:Bind Vm.Channel," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.CheckUpdates," },
        { "SettingsPage.xaml", "IsOn=\"{x:Bind Vm.RepoAutoupdate," },
        { "SettingsPage.xaml", "Command=\"{x:Bind Vm.InstallUpdateCommand}\"" },
        { "SettingsPage.xaml", "Command=\"{x:Bind Vm.RunWizardCommand}\"" },
        { "SettingsPage.xaml", "Command=\"{x:Bind Vm.SaveCommand}\"" },
        { "ShellPage.xaml", "Tag=\"wizard\"" },
    };

    /// <summary>Commands that only read, navigate, copy or edit locally: no lock needed.</summary>
    private static readonly HashSet<string> NotChanging =
    [
        "OpenCommand", "OpenConflictsCommand", "CloseMessageCommand", "ResetCommand", "NewStrategyCommand", "ImportCommand", "ExportCommand",
        "CheckConfigCommand", "LoadLogCommand", "CopyReportCommand", "SaveReportCommand", "LoadConflictsCommand", "CheckForUpdatesCommand",
        "RevertCommand", "StartServiceCommand", "RetryCommand",
        // the wizard is not reachable without the rights (menu item locked, Navigate refuses, never opens by itself)
        "GoFixCommand", "AutoSelectCommand", "RecheckCommand", "FinishCommand", "SkipCommand", "BackCommand", "NextCommand",
    ];

    [Theory]
    [MemberData(nameof(Controls))]
    public void The_control_is_locked_without_the_rights(string file, string anchor)
    {
        var xaml = File.ReadAllText(Path.Combine(Repo.Root, "src", "Zaprett.Ui", "Views", file));
        var at = xaml.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, anchor);
        var tag = xaml[xaml.LastIndexOf('<', at)..(xaml.IndexOf('>', at) + 1)];
        var enabled = Regex.Match(tag, "IsEnabled=\"([^\"]*)\"").Groups[1].Value;
        Assert.True(Regex.IsMatch(enabled, @"\bCan(Modify|Toggle|ChangeTrayAutostart)\b"), $"{file}: {anchor} -> IsEnabled=\"{enabled}\"");
        // x:Bind is OneTime by default: the rights may come after the page (or the list row) was built (D18)
        Assert.Contains("Mode=OneWay", enabled, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_command_on_the_pages_is_either_locked_or_known_to_change_nothing()
    {
        var locked = Controls.Select(row => (string)row[1]).ToHashSet();
        var views = Path.Combine(Repo.Root, "src", "Zaprett.Ui", "Views");
        var unknown = Directory.GetFiles(views, "*.xaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "Command=\"\\{x:Bind (?:Vm\\.)?(\\w+Command)\\}\"").Select(m => (m.Value, Name: m.Groups[1].Value)))
            .Where(c => !NotChanging.Contains(c.Name) && !locked.Contains(c.Value) && c.Name != "ActCommand")
            .Select(c => c.Value)
            .Distinct()
            .ToList();
        Assert.Empty(unknown);
    }
}

internal static class Repo
{
    /// <summary>The windows folder of the repository (the tests run from its bin folder).</summary>
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Zaprett.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Zaprett.slnx above " + AppContext.BaseDirectory);
    }
}
