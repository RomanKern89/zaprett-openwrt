using System.Diagnostics;
using System.Text.Json.Nodes;
using Zaprett.Cli;
using Zaprett.Ipc;

namespace Zaprett.Service.Tests;

internal sealed class FakeClient(Func<string, JsonObject?, JsonObject> answer) : IZaprettClient
{
    public List<(string Method, JsonObject? Args)> Calls { get; } = [];

    public Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default)
    {
        Calls.Add((method, args));
        return Task.FromResult(answer(method, args));
    }

    public async IAsyncEnumerable<ServiceEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class CliParserTests
{
    private static CliCommand Ok(params string[] argv)
    {
        var r = CliParser.Parse(argv);
        Assert.Null(r.Error);
        return r.Command!;
    }

    private static void Bad(params string[] argv) => Assert.NotNull(CliParser.Parse(argv).Error);

    [Theory]
    [InlineData("status", "status")]
    [InlineData("start", "start")]
    [InlineData("check", "check")]
    [InlineData("probe", "probe")]
    [InlineData("probe status", "probe.status")]
    [InlineData("monitor run", "monitor.run")]
    [InlineData("job log --tail 5", "job.log")]
    [InlineData("dns setup", "dns.setup")]
    [InlineData("dns off", "dns.setup")]
    [InlineData("autostart on", "autostart")]
    [InlineData("autostart off", "autostart")]
    [InlineData("repo fetch", "repo.fetch")]
    [InlineData("sources defaults", "sources.defaults")]
    [InlineData("conflicts", "conflicts")]
    [InlineData("settings get", "settings.get")]
    [InlineData("update check", "update.check")]
    [InlineData("page overview", "page")]
    public void Commands_MapToMethods(string line, string method) => Assert.Equal(method, Ok(line.Split(' ')).Method);

    [Fact]
    public void Arguments_UseRpcdNames()
    {
        Assert.Equal("zaprett-youtube", Ok("list", "enable", "zaprett-youtube").Args["id"]!.GetValue<string>());
        var w = Ok("wizard", "apply", "youtube", "discord:full");
        Assert.Equal("wizard.apply", w.Method);
        Assert.Equal(["youtube", "discord:full"], w.Args["services"]!.AsArray().Select(x => x!.GetValue<string>()));
        var t = Ok("test", "start", "--quick", "--strategies", "a,b");
        Assert.True(t.Args["quick"]!.GetValue<bool>());
        Assert.False(t.Args["exclusive"]!.GetValue<bool>());
        Assert.Equal(2, t.Args["strategies"]!.AsArray().Count);
        Assert.Equal(50, Ok("log", "--tail", "50").Args["tail"]!.GetValue<int>());
        Assert.True(Ok("repo", "upgrade", "--all").Args["all"]!.GetValue<bool>());
        Assert.Equal(2, Ok("repo", "install", "a", "b").Args["ids"]!.AsArray().Count);
        Assert.Equal(StdinKind.Text, Ok("strategy", "save", "user-x").Stdin);
        Assert.True(Ok("status", "--json").Json);
        Assert.True(Ok("dns", "setup").Args["enable"]!.GetValue<bool>());
        Assert.False(Ok("dns", "off").Args["enable"]!.GetValue<bool>());
        Assert.True(Ok("autostart", "on").Args["enable"]!.GetValue<bool>());
        Assert.False(Ok("autostart", "off").Args["enable"]!.GetValue<bool>());
        Assert.Equal("zh-CN", CliParser.Parse(["status", "--lang", "ZH-cn"]).Lang);
    }

    [Fact]
    public void BadArguments_AreRejected()
    {
        Bad("list", "enable");
        Bad("autostart");
        Bad("autostart", "yes");
        Bad("autostart", "on", "now");
        Bad("list", "enable", "../x");
        Bad("list", "enable", ".hidden");
        Bad("status", "extra");
        Bad("wizard", "apply");
        Bad("wizard", "apply", "a:b:c");
        Bad("repo", "upgrade");               // neither --all nor ids
        Bad("repo", "upgrade", "--all", "x"); // both
        Bad("log", "--tail", "-1");
        Bad("log", "--tail");
        Bad("status", "--unknown");
        Bad("fw", "apply");                   // router only
        Bad("gen-args");
        Bad("page", "nope");
        Bad("nonsense");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("-?")]
    [InlineData("help")]
    public void HelpSynonyms_AreHelp(string flag)
    {
        Assert.True(CliParser.Parse([flag]).Help);
        Assert.Null(CliParser.Parse([flag]).Error);
        Assert.True(CliParser.Parse(["status", flag]).Help || flag == "help");
        Assert.True(CliParser.IsHelpRequest([flag]));
    }

    [Fact]
    public void Help_WithLanguage()
    {
        var r = CliParser.Parse(["--help", "--lang", "zh-CN"]);
        Assert.True(r.Help);
        Assert.Equal("zh-CN", r.Lang);
        Assert.False(CliParser.IsHelpRequest([]));
        Assert.False(CliParser.IsHelpRequest(["status"]));
    }

    [Fact]
    public void Help_AndEmpty()
    {
        Assert.True(CliParser.Parse(["help"]).Help);
        Assert.True(CliParser.Parse([]).Help);
    }
}

public class CliAppTests
{
    /// <summary>Runs the CLI; lang "en" appends --lang en (the texts the assertions use), null leaves it out.</summary>
    private static async Task<(int Rc, string Out, string Err)> Run(string[] argv, IZaprettClient? client = null, string stdin = "",
        string? lang = "en")
    {
        var o = new StringWriter();
        var e = new StringWriter();
        string[] full = lang is null ? argv : [.. argv, "--lang", lang];
        int rc = await CliApp.RunAsync(full, () => client ?? new FakeClient((_, _) => new JsonObject { ["ok"] = true }),
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(stdin)), o, e);
        return (rc, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Language_ComesFromSettings_AndIsPassedToTheCall()
    {
        var client = new FakeClient((m, _) => m == "settings.get"
            ? JsonNode.Parse("""{"ok":true,"config":{"ui":{"language":"zh-CN"}}}""")!.AsObject()
            : new JsonObject { ["ok"] = false, ["error"] = "x", ["message"] = "m" });
        var (rc, _, err) = await Run(["start"], client, lang: null);
        Assert.Equal(1, rc);
        Assert.StartsWith("错误: m", err);
        Assert.Equal(["settings.get", "start"], client.Calls.Select(c => c.Method));
        Assert.Equal("zh-CN", client.Calls[1].Args!["lang"]!.GetValue<string>());
    }

    [Fact]
    public async Task Language_DefaultsToRussian_AndFlagOverrides()
    {
        var client = new FakeClient((m, _) => m == "settings.get"
            ? JsonNode.Parse("""{"ok":true,"config":{"ui":{"language":"en"}}}""")!.AsObject()
            : new JsonObject { ["ok"] = false, ["error"] = "x", ["message"] = "m" });
        var (_, _, en) = await Run(["start"], client, lang: null);
        Assert.StartsWith("Error: m", en);
        var (_, _, zh) = await Run(["start"], client, lang: "zh-cn");
        Assert.StartsWith("错误: m", zh);
        var noConfig = new FakeClient((_, _) => new JsonObject { ["ok"] = false, ["error"] = "x", ["message"] = "m" });
        var (_, _, ru) = await Run(["start"], noConfig, lang: null);
        Assert.StartsWith("Ошибка: m", ru);
        Assert.Equal(2, (await Run(["status"], client, lang: "de")).Rc);
    }

    [Fact]
    public async Task ServiceDown_IsDetectedOnce()
    {
        int calls = 0;
        var client = new FakeClient((_, _) =>
        {
            calls++;
            throw new ZaprettUnavailableException("down");
        });
        var (rc, _, err) = await Run(["status"], client, lang: null);
        Assert.Equal(3, rc);
        Assert.Equal(1, calls);
        Assert.Contains("Служба zaprett недоступна", err);
    }

    [Fact]
    public void Strings_AllLanguagesHaveTheSameKeys()
    {
        var ru = CliText.Read("ru").Select(p => p.Key).Order().ToList();
        foreach (var lang in new[] { "en", "zh-CN" })
            Assert.Equal(ru, CliText.Read(lang).Select(p => p.Key).Order().ToList());
        Assert.Contains("用法", CliText.Load("zh-CN").Usage);
        Assert.Equal("ru", CliText.Load("xx").Language);
    }

    // main.autostart apart from main.enabled: every language tells autostart, enable/disable and start/stop apart
    [Theory]
    [InlineData("ru", "при старте Windows")]
    [InlineData("en", "at Windows start")]
    [InlineData("zh-CN", "Windows 启动时")]
    public void Usage_ExplainsAutostart(string lang, string atWindowsStart)
    {
        var lines = CliText.Load(lang).Usage.Split('\n');
        Assert.Contains(lines, l => l.TrimStart().StartsWith("autostart on|off", StringComparison.Ordinal) && l.Contains(atWindowsStart));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("enable | disable", StringComparison.Ordinal) && l.Contains(atWindowsStart));
    }

    [Fact]
    public async Task UsageErrors_AreLocalized()
    {
        var (_, _, en) = await Run(["log", "--tail", "0"]);
        Assert.Contains("--tail needs a positive number", en);
        var (_, _, zh) = await Run(["fw", "apply"], lang: "zh-CN");
        Assert.Contains("“fw”是路由器专用命令", zh);
    }

    [Fact]
    public async Task Ok_Is0_JsonPrintedAsIs()
    {
        var client = new FakeClient((m, _) => new JsonObject { ["ok"] = true, ["running"] = true, ["name"] = "тест" });
        var (rc, output, _) = await Run(["status", "--json"], client);
        Assert.Equal(0, rc);
        var parsed = JsonNode.Parse(output)!;
        Assert.True(parsed["running"]!.GetValue<bool>());
        Assert.Equal("тест", parsed["name"]!.GetValue<string>());
        Assert.Contains("тест", output);   // not \u-escaped
    }

    [Fact]
    public async Task MethodError_Is1_OnStderr()
    {
        var client = new FakeClient((_, _) => new JsonObject { ["ok"] = false, ["error"] = "access_denied", ["message"] = "no" });
        var (rc, output, err) = await Run(["start"], client);
        Assert.Equal(1, rc);
        Assert.Empty(output);
        Assert.Contains("Error: no [access_denied]", err);
    }

    [Fact]
    public async Task BadArguments_Is2_WithoutCallingTheService()
    {
        var client = new FakeClient((_, _) => throw new InvalidOperationException("must not be called"));
        var (rc, _, err) = await Run(["list", "enable"], client);
        Assert.Equal(2, rc);
        Assert.Contains("Invalid arguments", err);
        Assert.Empty(client.Calls);
        Assert.Equal(2, (await Run([])).Rc);
        Assert.Equal(0, (await Run(["help"])).Rc);
        foreach (var f in new[] { "--help", "-h", "/?" })
        {
            var (hrc, hout, herr) = await Run([f]);
            Assert.Equal(0, hrc);
            Assert.StartsWith("Usage: zaprett", hout);
            Assert.Empty(herr);
        }
    }

    [Fact]
    public async Task ServiceDown_Is3()
    {
        await using var real = new ZaprettPipeClient("zaprett-none-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(200));
        var (rc, output, _) = await Run(["status", "--json"], real);
        Assert.Equal(3, rc);
        Assert.Equal("service_unavailable", JsonNode.Parse(output)!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task StrategySave_SendsStdinText_AndEnforcesLimit()
    {
        var client = new FakeClient((_, _) => new JsonObject { ["ok"] = true });
        var (rc, _, _) = await Run(["strategy", "save", "user-a"], client, "--dpi-desync=fake\n");
        Assert.Equal(0, rc);
        // trailing line breaks are removed from stdin (StdinText)
        Assert.Equal("--dpi-desync=fake", client.Calls[0].Args!["text"]!.GetValue<string>());
        Assert.Equal("user-a", client.Calls[0].Args!["id"]!.GetValue<string>());

        var big = new FakeClient((_, _) => new JsonObject { ["ok"] = true });
        var (rc2, _, _) = await Run(["strategy", "save", "user-a"], big, new string('x', CliParser.MaxStrategyBytes + 1));
        Assert.Equal(2, rc2);
        Assert.Empty(big.Calls);
    }

    [Fact]
    public async Task SourcesSave_MergesJson_NameFromCommandLineWins()
    {
        var client = new FakeClient((_, _) => new JsonObject { ["ok"] = true });
        await Run(["sources", "save", "my_list"], client, """{"name":"evil","url":"https://example.com/x.txt","enabled":true}""");
        var a = client.Calls.Single().Args!;
        Assert.Equal("my_list", a["name"]!.GetValue<string>());
        Assert.Equal("https://example.com/x.txt", a["url"]!.GetValue<string>());
        Assert.Equal(2, (await Run(["sources", "save", "my_list"], client, "not json")).Rc);
    }

    [Fact]
    public async Task HumanOutput_Status()
    {
        var client = new FakeClient((_, _) => JsonNode.Parse("""{"ok":true,"running":true,"engine_stats":{"pid":42},"platform":{"os":"Windows 11","version":"24H2","build":"26100.1","arch":"x64"},"windivert":{"foreign":["WinDivert14"]}}""")!.AsObject());
        var (rc, output, _) = await Run(["status"], client);
        Assert.Equal(0, rc);
        Assert.Contains("Engine: running (pid 42)", output);
        Assert.Contains("Windows 11 24H2 (26100.1, x64)", output);
        Assert.Contains("WinDivert14", output);
    }

    [Fact]
    public async Task Quiet_PrintsNothingOnSuccess()
    {
        var (rc, output, err) = await Run(["start", "--quiet"]);
        Assert.Equal(0, rc);
        Assert.Empty(output + err);
    }
}

/// <summary>The built zaprett-svc.exe and zaprett.exe as real processes: console mode, temp directories, own pipe.</summary>
public class EndToEndTests
{
    private static string Bin(string project, string tfm, string exe)
    {
        string dir = AppContext.BaseDirectory;
        // tests/Zaprett.Service.Tests/bin/<cfg>/<tfm>/ -> windows/
        var root = new DirectoryInfo(dir).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        string cfg = new DirectoryInfo(dir).Parent!.Name;
        return Path.Combine(root, "src", project, "bin", cfg, tfm, exe);
    }

    private static ProcessStartInfo Start(string exe, string pipe, string install, string data, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["ZAPRETT_PIPE"] = pipe;
        psi.Environment["ZAPRETT_INSTALL_DIR"] = install;
        psi.Environment["ZAPRETT_DATA_DIR"] = data;
        // the apphost must find the .NET 10 runtime this test runs on
        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location))))!;
        return psi;
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs).ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    [Fact]
    public async Task ConsoleService_AnswersCliStatus()
    {
        string svc = Bin("Zaprett.Service", "net10.0-windows", "zaprett-svc.exe");
        string cli = Bin("Zaprett.Cli", "net10.0", "zaprett.exe");
        Assert.True(File.Exists(svc), svc);
        Assert.True(File.Exists(cli), cli);
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "install"), data = Path.Combine(dir.Path, "data");
        string pipe = "zaprett-e2e-" + Guid.NewGuid().ToString("N");
        using var service = Process.Start(Start(svc, pipe, install, data, "--console", "--stub"))!;
        try
        {
            var serviceOut = service.StandardOutput.ReadToEndAsync();
            var serviceErr = service.StandardError.ReadToEndAsync();
            string logFile = Path.Combine(data, "logs", "zaprett.log");
            if (!await Wait.UntilAsync(() => ReadShared(logFile).Contains("ipc: listening"), TimeSpan.FromSeconds(30)))
            {
                bool exited = service.HasExited;
                if (!exited)
                    service.Kill(entireProcessTree: true);
                Assert.Fail($"service did not start (exited: {exited}); log: {ReadShared(logFile)}; stdout: {await serviceOut}; stderr: {await serviceErr}");
            }

            using var status = Process.Start(Start(cli, pipe, install, data, "status", "--json"))!;
            string output = await status.StandardOutput.ReadToEndAsync();
            await status.WaitForExitAsync();
            Assert.Equal(0, status.ExitCode);
            var json = JsonNode.Parse(output)!;
            Assert.True(json["ok"]!.GetValue<bool>());
            Assert.StartsWith("Windows 1", json["platform"]!["os"]!.GetValue<string>());
            Assert.NotNull(json["windivert"]!["foreign"]);

            // a modifying method from this (unelevated) test user is refused by the real token check
            using var start = Process.Start(Start(cli, pipe, install, data, "start", "--json"))!;
            string startOut = await start.StandardOutput.ReadToEndAsync();
            await start.WaitForExitAsync();
            bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            if (!elevated)
            {
                Assert.Equal(1, start.ExitCode);
                Assert.Equal("access_denied", JsonNode.Parse(startOut)!["error"]!.GetValue<string>());
            }
            Assert.DoesNotContain("ERROR", ReadShared(logFile));
        }
        finally
        {
            service.Kill(entireProcessTree: true);
            await service.WaitForExitAsync();
        }
    }
}
