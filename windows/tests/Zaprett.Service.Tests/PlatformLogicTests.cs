using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Service.Hosting;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

public class ScheduleTests
{
    private static JsonObject Config(bool enabled = true, bool watchdog = true, bool monitor = true, int interval = 30,
        bool autoupdate = true, int hour = 4) => new()
    {
        ["main"] = new JsonObject { ["enabled"] = enabled, ["watchdog"] = watchdog },
        ["monitor"] = new JsonObject { ["enabled"] = monitor, ["interval"] = interval },
        ["repo"] = new JsonObject { ["autoupdate"] = autoupdate, ["autoupdate_hour"] = hour },
    };

    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 23)));

    private static string[] Methods(IEnumerable<ScheduledCall> calls) => calls.Select(c => c.Method).ToArray();

    [Fact]
    public void Watchdog_EveryFiveMinutes()
    {
        var s = new Schedule();
        Assert.Equal(["ensure"], Methods(s.Due(T0, Config(monitor: false))));
        Assert.Empty(s.Due(T0.AddMinutes(4), Config(monitor: false)));
        Assert.Equal(["ensure"], Methods(s.Due(T0.AddMinutes(5), Config(monitor: false))));
    }

    [Fact]
    public void Nothing_WhenDisabledOrNoConfig()
    {
        var s = new Schedule();
        Assert.Empty(s.Due(T0, Config(enabled: false, autoupdate: false)));
        Assert.Empty(s.Due(T0, null));
        Assert.Empty(new Schedule().Due(T0, Config(watchdog: false, monitor: false, autoupdate: false)));
    }

    [Fact]
    public void Monitor_ByInterval_NotAtStart()
    {
        var s = new Schedule();
        var cfg = Config(watchdog: false, interval: 10);
        Assert.Empty(s.Due(T0, cfg));
        Assert.Empty(s.Due(T0.AddMinutes(9), cfg));
        Assert.Equal(["monitor.run"], Methods(s.Due(T0.AddMinutes(10), cfg)));
        Assert.Empty(s.Due(T0.AddMinutes(15), cfg));
        Assert.Equal(["monitor.run"], Methods(s.Due(T0.AddMinutes(20), cfg)));
    }

    [Fact]
    public void Autoupdate_OncePerDayAtTheHour()
    {
        var s = new Schedule();
        var cfg = Config(enabled: false, hour: 4);
        var at4 = new DateTimeOffset(new DateTime(2026, 9, 24, 4, 0, 30, DateTimeKind.Local));
        Assert.Empty(s.Due(at4.AddHours(-1), cfg));
        var calls = s.Due(at4, cfg);
        Assert.Equal(["autoupdate"], Methods(calls));
        Assert.Empty(s.Due(at4.AddMinutes(30), cfg));
        Assert.Equal(["autoupdate"], Methods(s.Due(at4.AddDays(1), cfg)));
    }

    [Fact]
    public void Autoupdate_ForEnabledSubscriptionEvenWhenRepoAutoupdateOff()
    {
        var at4 = new DateTimeOffset(new DateTime(2026, 9, 24, 4, 5, 0, DateTimeKind.Local));
        var cfg = Config(enabled: false, autoupdate: false);
        Assert.Empty(new Schedule().Due(at4, cfg));
        cfg["sources"] = new JsonObject { ["antifilter"] = new JsonObject { ["enabled"] = true } };
        Assert.Equal(["autoupdate"], Methods(new Schedule().Due(at4, cfg)));
    }
}

public class ConflictTests
{
    private const string Install = @"C:\Program Files\zaprett";

    private static ConflictSnapshot Snap(IEnumerable<ServiceEntry>? services = null, IEnumerable<ProcessEntry>? processes = null,
        IEnumerable<(string, string, NetworkInterfaceType)>? adapters = null, IEnumerable<string>? proxies = null, string install = Install) =>
        new((services ?? []).ToList(), (processes ?? []).ToList(), (adapters ?? []).ToList(), (proxies ?? []).ToList(), install);

    private static Dictionary<string, string> Items(ConflictSnapshot s) =>
        ConflictScanner.Evaluate(s).Select(i => (i!["id"]!.GetValue<string>(), i["severity"]!.GetValue<string>())).ToDictionary();

    private static List<JsonObject> All(ConflictSnapshot s) => ConflictScanner.Evaluate(s).Select(i => (JsonObject)i!).ToList();

    private static ProcessEntry Ours(int pid) => new(pid, "winws", $@"{Install}\engine\winws.exe", true);

    [Fact]
    public void CleanSystem_HasNoItems() => Assert.Empty(Items(Snap(
        services: [new("zaprett", "zaprett", true, $"\"{Install}\\zaprett-svc.exe\""), new("WinDivert", "WinDivert", true, $@"\??\{Install}\engine\WinDivert64.sys")],
        processes: [Ours(100), new(4, "explorer", @"C:\Windows\explorer.exe", false)])));

    [Fact]
    public void ForeignWinDivert_BlocksWhenLoaded_InfoWhenOnlyRegistered()
    {
        var all = All(Snap(services:
        [
            new("WinDivert", "WinDivert", true, @"\??\C:\GoodbyeDPI\x86_64\WinDivert64.sys"),
            new("WinDivert14", "WinDivert14", false, @"C:\old\WinDivert64.sys"),
        ]));
        var loaded = all.Single(i => i["id"]!.GetValue<string>() == "foreign_windivert");
        Assert.Equal("block", loaded["severity"]!.GetValue<string>());
        Assert.Equal("driver", loaded["kind"]!.GetValue<string>());
        Assert.Equal(@"C:\GoodbyeDPI\x86_64\WinDivert64.sys", loaded["path"]!.GetValue<string>());
        Assert.Contains(@"C:\GoodbyeDPI\x86_64\WinDivert64.sys", loaded["fix"]!.GetValue<string>());
        Assert.Equal("info", all.Single(i => i["id"]!.GetValue<string>() == "windivert_registered")["severity"]!.GetValue<string>());
    }

    [Fact]
    public void OurDriverFromAnotherFolder_IsForeign()
    {
        var items = Items(Snap(services: [new("WinDivert", "WinDivert", true, $@"\??\{Install}\old\WinDivert64.sys")]));
        Assert.Equal("block", items["foreign_windivert"]);
    }

    // D9: a foreign winws.exe run by hand shares the driver our winws loaded — only its WinDivert.dll shows it
    [Fact]
    public void ForeignWinwsProcess_UsingOurLoadedDriver_IsFound_WithPathAndPid()
    {
        var all = All(Snap(
            services: [new("WinDivert", "WinDivert", true, $@"\??\{Install}\engine\WinDivert64.sys")],
            processes: [Ours(100), new(6180, "winws", @"C:\zt\foreign\winws.exe", true)]));
        var item = Assert.Single(all);
        Assert.Equal("foreign_windivert_user", item["id"]!.GetValue<string>());
        Assert.Equal("block", item["severity"]!.GetValue<string>());
        Assert.Equal("process", item["kind"]!.GetValue<string>());
        Assert.Equal(6180, item["pid"]!.GetValue<int>());
        Assert.Equal(@"C:\zt\foreign\winws.exe", item["path"]!.GetValue<string>());
        Assert.Contains(@"C:\zt\foreign\winws.exe", item["detail"]!.GetValue<string>());
        Assert.Contains("pid 6180", item["detail"]!.GetValue<string>());
        string fix = item["fix"]!.GetValue<string>();
        Assert.Contains("taskkill /PID 6180", fix);
        Assert.DoesNotContain("service", fix, StringComparison.OrdinalIgnoreCase);   // it is no service
        Assert.DoesNotContain("Flowseal", fix);
    }

    [Fact]
    public void OurWinws_IsExcludedByPath_NotByName()
    {
        Assert.Empty(Items(Snap(processes: [Ours(100)])));
        // same name elsewhere is foreign, even without WinDivert.dll (name rule)
        var item = Assert.Single(All(Snap(processes: [new(7, "winws", @"C:\zapret-discord-youtube\bin\winws.exe", false)])));
        Assert.Equal("zapret", item["id"]!.GetValue<string>());
        Assert.Equal("process", item["kind"]!.GetValue<string>());
        Assert.Equal(7, item["pid"]!.GetValue<int>());
    }

    [Fact]
    public void Service_AdviceNamesTheService()
    {
        var all = All(Snap(services: [new("zapret", "zapret", true, @"C:\flowseal\bin\winws.exe"), new("GoodbyeDPI", "GoodbyeDPI", false, @"C:\gdpi\goodbyedpi.exe")]));
        var z = all.Single(i => i["id"]!.GetValue<string>() == "zapret");
        Assert.Equal("service", z["kind"]!.GetValue<string>());
        Assert.Equal("zapret", z["service"]!.GetValue<string>());
        Assert.Contains("sc stop \"zapret\"", z["fix"]!.GetValue<string>());
        Assert.Contains("start= disabled", z["fix"]!.GetValue<string>());
        Assert.Equal("block", z["severity"]!.GetValue<string>());
        Assert.Equal("warn", all.Single(i => i["id"]!.GetValue<string>() == "goodbyedpi")["severity"]!.GetValue<string>());   // installed, not running
    }

    [Fact]
    public void RunningService_HasItsPidAndExe_AsNumbersAndPaths()
    {
        var z = Assert.Single(All(Snap(services: [new("zapret", "zapret", true, "\"C:\\flowseal\\bin\\winws.exe\" --wf-tcp=443", 4321)])));
        Assert.Equal(4321, z["pid"]!.GetValue<int>());                 // a JSON number, not a string
        Assert.Equal(System.Text.Json.JsonValueKind.Number, z["pid"]!.GetValueKind());
        Assert.Equal(@"C:\flowseal\bin\winws.exe", z["path"]!.GetValue<string>());
        var stopped = Assert.Single(All(Snap(services: [new("zapret", "zapret", false, @"C:\flowseal\bin\winws.exe --x")])));
        Assert.Null(stopped["pid"]);
        Assert.Equal(@"C:\flowseal\bin\winws.exe", stopped["path"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\x\\a.exe\" --run", @"C:\Program Files\x\a.exe")]
    [InlineData(@"C:\x\a.exe --run", @"C:\x\a.exe")]
    [InlineData(@"C:\x\a.exe", @"C:\x\a.exe")]
    public void ServiceExe_IsCutFromImagePath(string image, string exe) => Assert.Equal(exe, ConflictScanner.ServiceExe(image));

    [Fact]
    public void ProcessPid_IsANumber()
    {
        var item = Assert.Single(All(Snap(processes: [new(6180, "winws", @"C:\zt\foreign\winws.exe", true)])));
        Assert.Equal(System.Text.Json.JsonValueKind.Number, item["pid"]!.GetValueKind());
    }

    [Fact]
    public void ServiceProcess_IsNotReportedTwice()
    {
        var all = All(Snap(
            services: [new("zapret", "zapret", true, "\"C:\\flowseal\\bin\\winws.exe\" --wf-tcp=443")],
            processes: [new(9, "winws", @"C:\flowseal\bin\winws.exe", false)]));
        Assert.Single(all, i => i["id"]!.GetValue<string>() == "zapret");
    }

    [Fact]
    public void WinDivertUser_IsNotReportedAgainByName()
    {
        var all = All(Snap(processes: [new(11, "goodbyedpi", @"C:\gdpi\goodbyedpi.exe", true)]));
        var item = Assert.Single(all);
        Assert.Equal("foreign_windivert_user", item["id"]!.GetValue<string>());
    }

    [Fact]
    public void PathUnknown_IsSaid()
    {
        var item = Assert.Single(All(Snap(processes: [new(12, "winws", null, true)])));
        Assert.Contains("path unknown", item["detail"]!.GetValue<string>());
    }

    [Fact]
    public void OtherTools_AreFound()
    {
        var items = Items(Snap(processes: [new(1, "AdguardSvc", @"C:\Program Files\Adguard\AdguardSvc.exe", false), new(2, "KillerNetworkService", null, false)]));
        Assert.Equal("warn", items["adguard"]);
        Assert.Equal("warn", items["killer"]);
    }

    [Fact]
    public void VpnAndProxy_AreReported()
    {
        var items = Items(Snap(
            adapters: [("wt0", "WireGuard Tunnel", NetworkInterfaceType.Unknown), ("Ethernet", "Intel(R) Ethernet", NetworkInterfaceType.Ethernet)],
            proxies: ["127.0.0.1:8080"], install: @"C:\Программы\zaprett"));
        Assert.Equal("info", items["vpn:wt0"]);
        Assert.False(items.ContainsKey("vpn:Ethernet"));
        Assert.Equal("info", items["proxy"]);
        Assert.False(items.ContainsKey("install-path"));
    }

    [Fact]
    public void ForeignUsers_ExcludeOurFolderByPath()
    {
        var users = ProcessSnapshot.ForeignWinDivertUsers(
            [Ours(1), new(2, "winws", @"C:\zt\foreign\winws.exe", true), new(3, "x", @"C:\Program Files\zaprett-old\x.exe", true),
             new(4, "y", @"C:\y.exe", false)], Install);
        Assert.Equal([2, 3], users.Select(u => u.Pid));   // "zaprett-old" is not our folder
    }
}

/// <summary>Module detection on this machine, read only.</summary>
public class ProcessSnapshotTests
{
    [Fact]
    public void Modules_OfOwnProcess()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        Assert.True(ProcessSnapshot.HasModule(me, "kernel32.dll"));
        Assert.False(ProcessSnapshot.HasModule(me, ProcessSnapshot.WinDivertModule));
        var snap = ProcessSnapshot.Take();
        var self = snap.Single(p => p.Pid == me.Id);
        Assert.Equal(me.MainModule!.FileName, self.Path);
        Assert.False(self.UsesWinDivert);
    }

    [Fact]
    public void Candidates_HaveWinDivertDllBesideTheExe()
    {
        using var dir = new TempDir();
        string exe = Path.Combine(dir.Path, "winws.exe");
        Assert.False(ProcessSnapshot.HasWinDivertBeside(exe));
        File.WriteAllBytes(Path.Combine(dir.Path, ProcessSnapshot.WinDivertModule), [0]);
        Assert.True(ProcessSnapshot.HasWinDivertBeside(exe));
    }

    [Fact]
    public void LoadedWinDivertDll_IsSeen()
    {
        // any DLL loaded under the name WinDivert.dll (a copy of version.dll: loading needs no driver)
        using var dir = new TempDir();
        string fake = Path.Combine(dir.Path, ProcessSnapshot.WinDivertModule);
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), fake);
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(ProcessSnapshot.HasModule(me, ProcessSnapshot.WinDivertModule));   // negative control
        var lib = System.Runtime.InteropServices.NativeLibrary.Load(fake);
        Assert.True(ProcessSnapshot.HasModule(me, ProcessSnapshot.WinDivertModule));
        System.Runtime.InteropServices.NativeLibrary.Free(lib);
    }
}


public class DnsAndFirewallTests
{
    private static string Decode(IReadOnlyList<string> psArgs) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(psArgs[psArgs.Count - 1]));

    [Fact]
    public async Task Firewall_AddsRuleOnlyWhenMissing()
    {
        var runner = new FakeRunner();
        bool exists = false;
        runner.Answer = (_, a) =>
        {
            if (a[2] == "show")
                return new ProcessResult(exists ? 0 : 1, "", "", false);
            if (a[2] == "add")
                exists = true;
            if (a[2] == "delete")
                exists = false;
            return new ProcessResult(0, "", "", false);
        };
        var fw = new FirewallControl(runner, new MemoryLog(), @"C:\Windows\System32\netsh.exe");
        await fw.SetQuicBlockAsync(true, default);
        await fw.SetQuicBlockAsync(true, default);
        Assert.Equal(1, runner.Calls.Count(c => c.Args[2] == "add"));
        var add = runner.Calls.Single(c => c.Args[2] == "add").Args;
        Assert.Equal(["advfirewall", "firewall", "add", "rule", "name=zaprett QUIC block", "dir=out", "action=block", "protocol=UDP", "remoteport=443"],
            add.Take(9));
        Assert.True(await fw.IsQuicBlockedAsync(default));
        await fw.SetQuicBlockAsync(false, default);
        await fw.SetQuicBlockAsync(false, default);
        Assert.Equal(1, runner.Calls.Count(c => c.Args[2] == "delete"));
        Assert.False(await fw.IsQuicBlockedAsync(default));
    }

    [Fact]
    public async Task Firewall_NetshFailure_Throws()
    {
        var runner = new FakeRunner { Answer = (_, a) => new ProcessResult(1, "", "denied", false) };
        var fw = new FirewallControl(runner, new MemoryLog(), "netsh.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fw.SetQuicBlockAsync(true, default));
    }

    [Fact]
    public async Task Dns_Windows10_IsNotSupported_AndRunsNothing()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner();
        var dns = new DnsControl(runner, new WindowsPaths(dir.Path, dir.Path), new MemoryLog(), osBuild: 19045);
        var r = await dns.SetupAsync(true, default);
        Assert.Equal("not_supported", r["error"]!.GetValue<string>());
        Assert.False((await dns.GetStatusAsync(default))["encrypted"]!.GetValue<bool>());
        Assert.Empty(runner.Calls);
    }

    private const string Guid12 = "{11111111-2222-3333-4444-555555555555}";

    // VM state from SPIKE-M1 §6: DHCP DNS, built-in DoH entries for Cloudflare, empty Doh key
    private static string State(string servers, bool dhcp, string dohFlags = "[]", string servers6 = "[]") =>
        "{\"doh\":[{\"server\":\"1.1.1.1\",\"template\":\"https://cloudflare-dns.com/dns-query\",\"fallback\":false,\"auto\":false}]," +
        "\"well_known\":[{\"server\":\"1.1.1.1\",\"key\":true,\"flags\":false},{\"server\":\"1.0.0.1\",\"key\":false,\"flags\":false}]," +
        "\"interfaces\":{\"index\":12,\"guid\":\"" + Guid12 + "\",\"name\":\"Ethernet\",\"servers\":" + servers + ",\"servers6\":" + servers6 +
        ",\"dhcp\":" + (dhcp ? "true" : "false") + ",\"doh_flags\":" + dohFlags + "}}";

    [Fact]
    public async Task Dns_Windows11_EnableSavesStateAndRestoresExactly()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, dir.Path);
        paths.EnsureDataDirs();
        var runner = new FakeRunner();
        string state = State("[\"192.168.1.200\"]", dhcp: true);
        runner.Answer = (_, a) => Decode(a).Contains("ConvertTo-Json", StringComparison.Ordinal)
            ? new ProcessResult(0, state, "", false)
            : new ProcessResult(0, "", "", false);
        var dns = new DnsControl(runner, paths, new MemoryLog(), osBuild: 26100);

        var on = await dns.SetupAsync(true, default);
        Assert.True(on["ok"]!.GetValue<bool>(), on.ToJsonString());
        string enable = Decode(runner.Calls[1].Args);
        string key = @"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\" + Guid12 + @"\DohInterfaceSettings\Doh\1.1.1.1";
        Assert.Contains($"New-Item -Path '{key}' -Force", enable);
        Assert.Contains($"New-ItemProperty -Path '{key}' -Name DohFlags -PropertyType QWord -Value 1 -Force", enable);
        Assert.Contains("Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses @('1.1.1.1','1.0.0.1')", enable);
        // 1.1.1.1 already has a built-in entry: only 1.0.0.1 is added, never with UDP fallback
        Assert.DoesNotContain("-ServerAddress '1.1.1.1' -DohTemplate", enable);
        Assert.Contains("Add-DnsClientDohServerAddress -ServerAddress '1.0.0.1' -DohTemplate 'https://cloudflare-dns.com/dns-query' -AllowFallbackToUdp $False", enable);
        Assert.True(File.Exists(Path.Combine(paths.RunDir, "dns-backup.json")));
        Assert.All(runner.Calls, c => Assert.EndsWith("powershell.exe", c.File, StringComparison.OrdinalIgnoreCase));

        // a second enable must keep the first saved state (the one before any change)
        state = State("[\"1.1.1.1\"]", dhcp: false);
        await dns.SetupAsync(true, default);
        Assert.Contains("192.168.1.200", File.ReadAllText(Path.Combine(paths.RunDir, "dns-backup.json")));

        var off = await dns.SetupAsync(false, default);
        Assert.True(off["changed"]!.GetValue<bool>());
        string restore = Decode(runner.Calls[^1].Args);
        Assert.Contains("Set-DnsClientServerAddress -InterfaceIndex 12 -ResetServerAddresses", restore);   // was DHCP
        Assert.Contains($"Remove-Item -Path '{key}' -Recurse", restore);
        Assert.Contains("Remove-DnsClientDohServerAddress -ServerAddress '1.0.0.1'", restore);            // we added it
        Assert.DoesNotContain("Remove-DnsClientDohServerAddress -ServerAddress '1.1.1.1'", restore);      // built-in, keep
        Assert.Contains(@"Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohWellKnownServers\1.1.1.1' -Name Flags", restore);
        Assert.False(File.Exists(Path.Combine(paths.RunDir, "dns-backup.json")));
    }

    [Fact]
    public void Dns_RestoreOfStaticServers_AndKeepsFlagsThatExisted()
    {
        var backup = (JsonObject)JsonNode.Parse(State("[\"8.8.8.8\",\"8.8.4.4\"]", dhcp: false,
            dohFlags: "[{\"server\":\"1.1.1.1\",\"flags\":1}]"))!;
        string s = DnsControl.RestoreScript(backup);
        Assert.Contains("Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses @('8.8.8.8','8.8.4.4')", s);
        Assert.DoesNotContain(@"\Doh\1.1.1.1' -Recurse", s);   // it was there before us
        Assert.Contains(@"\Doh\1.0.0.1' -Recurse", s);
        Assert.Contains("-Name Flags", s);
        // a Flags value that existed before us stays
        backup["well_known"]![0]!["flags"] = true;
        Assert.DoesNotContain("-Name Flags", DnsControl.RestoreScript(backup));
    }

    [Theory]
    [InlineData("[\"1.1.1.1'; Remove-Item C:\\\\ -Recurse; '\"]")]
    [InlineData("[\"fe80::1%12\"]")]
    public void Dns_RestoreScript_RejectsNonAddresses(string servers) =>
        Assert.Throws<ArgumentException>(() => DnsControl.RestoreScript((JsonObject)JsonNode.Parse(State(servers, dhcp: false))!));

    [Fact]
    public void Dns_RejectsBadGuid() =>
        Assert.Throws<ArgumentException>(() => DnsControl.EnableScript([(12, "{x'; rm -r C:\\; '}")], []));

    [Theory]
    [InlineData("[\"1.1.1.1\",\"1.0.0.1\"]", "[{\"server\":\"1.1.1.1\",\"flags\":1},{\"server\":\"1.0.0.1\",\"flags\":1}]", "[]", false)]  // 1.0.0.1 has no DoH entry
    [InlineData("[\"1.1.1.1\"]", "[{\"server\":\"1.1.1.1\",\"flags\":1}]", "[]", true)]
    [InlineData("[\"1.1.1.1\"]", "[]", "[]", false)]                                                        // no adapter flag
    [InlineData("[\"1.1.1.1\"]", "[{\"server\":\"1.1.1.1\",\"flags\":0}]", "[]", false)]
    [InlineData("[\"1.1.1.1\",\"192.168.1.1\"]", "[{\"server\":\"1.1.1.1\",\"flags\":1}]", "[]", false)]    // one plain server
    [InlineData("[\"1.1.1.1\"]", "[{\"server\":\"1.1.1.1\",\"flags\":1}]", "[\"fd00::1\"]", false)]         // plain IPv6 DNS
    public void Dns_EncryptedOnlyWithAdapterFlagAndEntry(string servers, string flags, string servers6, bool encrypted)
    {
        var r = DnsControl.Evaluate((JsonObject)JsonNode.Parse(State(servers, dhcp: false, flags, servers6))!);
        Assert.Equal(encrypted, r["encrypted"]!.GetValue<bool>());
        if (encrypted)
            Assert.Equal("cloudflare-dns.com", r["provider"]!.GetValue<string>());
    }

    [Fact]
    public void DohJson_TakesOnlyARecords()
    {
        var ips = DnsResolver.ParseDohJson("""{"Status":0,"Answer":[{"type":5,"data":"x.example."},{"type":1,"data":"142.250.1.1"},{"type":1,"data":"bad"},{"type":28,"data":"::1"}]}""");
        Assert.Equal(["142.250.1.1"], ips);
        Assert.Empty(DnsResolver.ParseDohJson("""{"Status":3}"""));
        Assert.Throws<FormatException>(() => DnsResolver.ParseDohJson("[1]"));
    }
}

/// <summary>Touch the real system, read-only. Not run by default: dotnet test --filter Category=System.</summary>
[Trait("Category", "System")]
public class SystemReadOnlyTests
{
    [Fact]
    public async Task Netsh_ShowRule_QuotingOfNamesWithSpaces()
    {
        var runner = new ProcessRunner();
        string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        var missing = await runner.RunAsync(netsh, FirewallControl.ShowArgs(), TimeSpan.FromSeconds(30), default);
        Assert.Equal(1, missing.ExitCode);   // "zaprett QUIC block" is not on this workstation
        var existing = await runner.RunAsync(netsh, ["advfirewall", "firewall", "show", "rule", "name=Microsoft 365 Copilot"], TimeSpan.FromSeconds(30), default);
        Assert.Equal(0, existing.ExitCode);
        var wrong = await runner.RunAsync(netsh, ["advfirewall", "firewall", "show", "rule", "name=Microsoft 365 Copilotx"], TimeSpan.FromSeconds(30), default);
        Assert.Equal(1, wrong.ExitCode);
    }

    [Fact]
    public async Task SystemInfo_AndConflicts_Read()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, dir.Path);
        var log = new MemoryLog();
        var p = await new SystemInfo(paths, log).GetPlatformAsync(default);
        Assert.StartsWith("Windows 1", p["os"]!.GetValue<string>());
        Assert.NotNull(await new ConflictScanner(paths, log).ScanAsync(default));
    }

    [Fact]
    public async Task DynamicPorts_AreReadFromNetsh()
    {
        var ranges = await new NetshDynamicPorts(new ProcessRunner()).GetTcpAsync(default);
        Assert.NotNull(ranges);
        Assert.Equal(2, ranges.Count);
        Assert.All(ranges, r => Assert.InRange(r.Start, 1, 65535));
    }

    [Fact]
    public void RunningServices_HavePids()
    {
        var list = SystemInfo.ListServices(System.ServiceProcess.ServiceController.GetServices());
        var eventLog = list.Single(s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
        Assert.True(eventLog.Running);
        Assert.True(eventLog.Pid > 0);
        Assert.All(list.Where(s => !s.Running), s => Assert.Null(s.Pid));
    }

    [Fact]
    public async Task DynamicPorts_AreReadFromWmi()
    {
        var ranges = await new WmiDynamicPorts(new MemoryLog()).GetTcpAsync(default);
        Assert.NotNull(ranges);
        Assert.All(ranges, r => Assert.InRange(r.Start, 1, 65535));
    }

    [Fact]
    public async Task Resolver_SystemAndDoh()
    {
        using var r = new DnsResolver();
        Assert.NotEmpty(await r.ResolveSystemAsync("localhost", default));
        Assert.NotEmpty(await r.ResolveDohAsync("example.com", default));
    }
}

public class DynamicPortsTests
{
    [Theory]
    [InlineData("Protocol tcp Dynamic Port Range\n---------------------------------\nStart Port      : 49152\nNumber of Ports : 16384\n", 49152, 16384)]
    [InlineData("Протокол tcp Диапазон динамических портов\r\n---\r\nНачальный порт      : 10000\r\nЧисло портов : 55536\r\n", 10000, 55536)]
    public void Netsh_IsParsed_InAnyLanguage(string output, int start, int count) =>
        Assert.Equal(new PortRange(start, count), NetshDynamicPorts.Parse(output));

    [Theory]
    [InlineData("")]
    [InlineData("The requested operation requires elevation.")]
    [InlineData("Start Port : 60000\nNumber of Ports : 16384\n")]   // beyond 65535
    public void Netsh_Garbage_IsNull(string output) => Assert.Null(NetshDynamicPorts.Parse(output));

    [Fact]
    public async Task BothFamilies_AreRead_AndAFailureMeansUnknown()
    {
        var runner = new FakeRunner
        {
            Answer = (_, a) => new ProcessResult(0, a[1] == "ipv4" ? "Start Port : 49152\nNumber of Ports : 16384" : "Start Port : 1025\nNumber of Ports : 64511", "", false),
        };
        var ranges = await new NetshDynamicPorts(runner, "netsh.exe").GetTcpAsync(default);
        Assert.Equal([new PortRange(49152, 16384), new PortRange(1025, 64511)], ranges);
        Assert.Equal([["int", "ipv4", "show", "dynamicport", "tcp"], ["int", "ipv6", "show", "dynamicport", "tcp"]], runner.Calls.Select(c => c.Args.ToArray()));
        Assert.True(ranges![1].Overlaps(40000, 40100));
        Assert.False(ranges[0].Overlaps(40000, 40100));
        runner.Answer = (_, _) => new ProcessResult(1, "", "denied", false);
        Assert.Null(await new NetshDynamicPorts(runner, "netsh.exe").GetTcpAsync(default));
    }

    [Theory]
    [InlineData(40101, 100, false)]
    [InlineData(39900, 100, false)]
    [InlineData(39900, 101, true)]
    [InlineData(40100, 1, true)]
    public void Overlap_Edges(int start, int count, bool overlaps) =>
        Assert.Equal(overlaps, new PortRange(start, count).Overlaps(40000, 40100));
}
