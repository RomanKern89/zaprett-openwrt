using System.Text.Json.Nodes;
using Microsoft.Win32;
using Zaprett.Core;
using Zaprett.Service.Hosting;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>"Show the icon at Windows sign-in": the Run value written and deleted by the service, the saved choice
/// applied at the start. The registry here is a scratch key under HKCU (deleted after each test), not HKLM.</summary>
public sealed class TrayAutostartTests : IDisposable
{
    private readonly string _root = @"Software\zaprett-tests\" + Guid.NewGuid().ToString("N");
    private readonly MemoryLog _log = new();
    private const string UiExe = @"C:\Program Files\zaprett\zaprett-ui.exe";

    private RegistryKey Run() => Registry.CurrentUser.CreateSubKey(_root + @"\Run", writable: true);
    private RegistryKey Settings() => Registry.CurrentUser.CreateSubKey(_root + @"\zaprett", writable: true);
    private TrayAutostart Tray() => new(UiExe, _log, Run, Settings);

    private object? RunValue()
    {
        using var k = Run();
        return k.GetValue(TrayAutostart.RunValueName);
    }

    private object? Choice()
    {
        using var k = Settings();
        return k.GetValue(TrayAutostart.ChoiceValueName);
    }

    private void SetRun(string value)
    {
        using var k = Run();
        k.SetValue(TrayAutostart.RunValueName, value);
    }

    private void SetChoice(int value)
    {
        using var k = Settings();
        k.SetValue(TrayAutostart.ChoiceValueName, value, RegistryValueKind.DWord);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        bool empty;
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\zaprett-tests"))
            empty = parent is { SubKeyCount: 0, ValueCount: 0 };
        if (empty)
            Registry.CurrentUser.DeleteSubKey(@"Software\zaprett-tests", throwOnMissingSubKey: false);
    }

    [Fact]
    public void On_WritesWhatTheInstallerWrites_AndRemembers()
    {
        Assert.True(Tray().Set(true));
        Assert.Equal("\"C:\\Program Files\\zaprett\\zaprett-ui.exe\" --tray", RunValue());
        Assert.Equal(1, Choice());
        Assert.True(Tray().IsOn());
    }

    [Fact]
    public void Off_DeletesTheValue_AndRemembers()
    {
        Tray().Set(true);
        Assert.False(Tray().Set(false));
        Assert.Null(RunValue());
        Assert.Equal(0, Choice());
        Assert.False(Tray().IsOn());
    }

    // a Run\zaprett of somebody else (another program, another path) is not ours: not "on", and not deleted
    [Fact]
    public void ForeignValue_IsNotOn_AndIsNotDeleted()
    {
        SetRun("\"C:\\Other\\zaprett-ui.exe\" --tray");
        Assert.False(Tray().IsOn());
        Tray().Set(false);
        Assert.Equal("\"C:\\Other\\zaprett-ui.exe\" --tray", RunValue());
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\zaprett\\zaprett-ui.exe\" --tray", true)]
    [InlineData("\"c:\\program files\\ZAPRETT\\zaprett-ui.exe\"", true)]
    [InlineData("C:\\Tools\\zaprett-ui.exe --tray", false)]
    [InlineData("", false)]
    public void IsOn_ComparesTheExe(string value, bool expected)
    {
        SetRun(value);
        Assert.Equal(expected, Tray().IsOn());
    }

    // a repair re-created the value the user had switched off: the service start removes it again
    [Fact]
    public void SavedOff_ValuePresent_IsRemovedAtStart()
    {
        SetChoice(0);
        SetRun("\"C:\\Program Files\\zaprett\\zaprett-ui.exe\" --tray");
        Tray().ApplyRemembered();
        Assert.Null(RunValue());
        Assert.Contains(_log.Lines, l => l.Contains("did not match the saved choice (off), corrected"));
    }

    // the old product's removal during an update deleted the value: the service start writes it again
    [Fact]
    public void SavedOn_ValueGone_IsWrittenAtStart()
    {
        SetChoice(1);
        Tray().ApplyRemembered();
        Assert.Equal("\"C:\\Program Files\\zaprett\\zaprett-ui.exe\" --tray", RunValue());
    }

    // negative control: no saved choice (an install made before the switch existed): the Run value is left as it is
    [Fact]
    public void NoSavedChoice_NothingTouched()
    {
        SetRun("\"C:\\Program Files\\zaprett\\zaprett-ui.exe\" --tray");
        Tray().ApplyRemembered();
        Assert.NotNull(RunValue());
        Assert.Empty(_log.Lines);
    }

    private sealed class Core : ICommandDispatcher
    {
        public IReadOnlySet<string> ReadOnlyMethods { get; } = new HashSet<string> { "status" };
        public event Action<string, JsonObject>? Event;
        public List<string> Calls { get; } = [];

        public Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct)
        {
            Calls.Add(method);
            Event?.Invoke("never", new JsonObject());
            return Task.FromResult(new JsonObject { ["ok"] = true, ["running"] = true });
        }
    }

    private static readonly CallerInfo Operator = new("u", false, true);
    private static readonly CallerInfo Plain = new("u", false, false);

    // negative control: a plain user gets access_denied and the registry does not change
    [Fact]
    public async Task PlainUser_IsDenied_RegistryUnchanged()
    {
        var core = new Core();
        var m = new ServiceMethods(core, Tray(), _log);
        var r = await m.InvokeAsync("tray.autostart", new JsonObject { ["enable"] = true, ["lang"] = "en" }, Plain, default);
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("access_denied", r["error"]!.GetValue<string>());
        Assert.Null(RunValue());
        Assert.Null(Choice());
        Assert.Empty(core.Calls);
        // and the pipe server refuses it before the dispatcher: it is not a read-only method
        Assert.DoesNotContain("tray.autostart", m.ReadOnlyMethods);
    }

    [Fact]
    public async Task Operator_SwitchesItOnAndOff()
    {
        var m = new ServiceMethods(new Core(), Tray(), _log);
        var on = await m.InvokeAsync("tray.autostart", new JsonObject { ["enable"] = true }, Operator, default);
        Assert.True(on["ok"]!.GetValue<bool>());
        Assert.True(on["tray_autostart"]!.GetValue<bool>());
        Assert.NotNull(RunValue());
        var off = await m.InvokeAsync("tray.autostart", new JsonObject { ["enable"] = false }, Operator, default);
        Assert.False(off["tray_autostart"]!.GetValue<bool>());
        Assert.Null(RunValue());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public async Task BadArgument_IsInvalidArgument(string? enableJson)
    {
        var args = new JsonObject();
        if (enableJson is not null)
            args["enable"] = JsonNode.Parse(enableJson);
        var r = await new ServiceMethods(new Core(), Tray(), _log).InvokeAsync("tray.autostart", args, Operator, default);
        Assert.Equal("invalid_argument", r["error"]!.GetValue<string>());
        Assert.Null(RunValue());
    }

    private sealed class PageCore : ICommandDispatcher
    {
        public IReadOnlySet<string> ReadOnlyMethods { get; } = new HashSet<string> { "page" };
        public event Action<string, JsonObject>? Event;

        public Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct)
        {
            Event?.Invoke("never", new JsonObject());
            return Task.FromResult(new JsonObject
            {
                ["ok"] = true, ["page"] = "overview",
                ["status"] = new JsonObject { ["ok"] = true, ["running"] = true },
                ["job"] = new JsonObject { ["ok"] = true },
            });
        }
    }

    // D18: the UI keeps its state from "page", whose status part must carry the field as "status" does
    [Fact]
    public async Task PageStatus_CarriesTheRegistryState()
    {
        var m = new ServiceMethods(new PageCore(), Tray(), _log);
        var off = await m.InvokeAsync("page", new JsonObject { ["name"] = "overview" }, Plain, default);
        Assert.False(off["status"]!["tray_autostart"]!.GetValue<bool>());
        Assert.Null(off["job"]!["tray_autostart"]);
        Assert.Null(off["tray_autostart"]);
        SetRun("\"C:\\Program Files\\zaprett\\zaprett-ui.exe\" --tray");
        var on = await m.InvokeAsync("page", new JsonObject { ["name"] = "overview" }, Plain, default);
        Assert.True(on["status"]!["tray_autostart"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Status_CarriesTheRegistryState_OtherMethodsUntouched()
    {
        var core = new Core();
        var m = new ServiceMethods(core, Tray(), _log);
        Assert.False((await m.InvokeAsync("status", null, Plain, default))["tray_autostart"]!.GetValue<bool>());
        SetRun("\"C:\\Program Files\\zaprett\\zaprett-ui.exe\" --tray");
        Assert.True((await m.InvokeAsync("status", null, Plain, default))["tray_autostart"]!.GetValue<bool>());
        var other = await m.InvokeAsync("check", null, Operator, default);
        Assert.Null(other["tray_autostart"]);
        Assert.Equal(["status", "status", "check"], core.Calls);
    }
}
