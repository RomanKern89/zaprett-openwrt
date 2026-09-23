using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Service.Hosting;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

public class IsolationFilterTests
{
    private const string Saved = "outbound and !impostor and !loopback and ((ip and tcp.DstPort == 443))";

    [Fact]
    public void Filters_AreTheSpikeRecipe_WithoutNegatedGroups()
    {
        Assert.Equal("(" + Saved + ") and (!tcp or (outbound and (tcp.SrcPort < 40000 or tcp.SrcPort > 40100)) or (inbound and (tcp.DstPort < 40000 or tcp.DstPort > 40100)))",
            EngineIsolation.MainFilter(Saved, 40000, 40100));
        Assert.Equal("(" + Saved + ") and tcp and ((outbound and tcp.SrcPort >= 40000 and tcp.SrcPort <= 40100) or (inbound and tcp.DstPort >= 40000 and tcp.DstPort <= 40100))",
            EngineIsolation.CandidateFilter(Saved, 40000, 40100));
        // WinDivert rejects "!(" and "not (" (SPIKE-M1 §5)
        foreach (var f in new[] { EngineIsolation.MainFilter(Saved, 1, 2), EngineIsolation.CandidateFilter(Saved, 1, 2) })
        {
            Assert.DoesNotContain("!(", f.Replace(Saved, ""));
            Assert.DoesNotContain("not (", f);
        }
    }

    [Fact]
    public void FilterOptions_AreStripped()
    {
        var args = EngineIsolation.WithoutFilterOptions(["--wf-l3=ipv4", "--wf-tcp=80,443", "--wf-udp=443", "--wf-raw-part=x", "--filter-tcp=443",
            "--dpi-desync=fake", "--new", "--wf-save=a"]);
        Assert.Equal(["--filter-tcp=443", "--dpi-desync=fake", "--new"], args);
    }

    private static (EngineIsolation Iso, FakeRunner Runner, WindowsPaths Paths) Make(TempDir dir, int exitCode = 0, string saved = Saved)
    {
        var paths = new WindowsPaths(dir.Path, dir.Path);
        paths.EnsureDataDirs();
        var runner = new FakeRunner
        {
            Answer = (_, a) =>
            {
                var save = a.FirstOrDefault(x => x.StartsWith("--wf-save=", StringComparison.Ordinal));
                if (save is not null && exitCode == 0)
                    File.WriteAllText(save["--wf-save=".Length..], saved + "\n");
                return new ProcessResult(exitCode, "", exitCode == 0 ? "" : "bad option", false);
            },
        };
        return (new EngineIsolation(runner, paths, new MemoryLog()), runner, paths);
    }

    [Fact]
    public async Task Isolate_UsesWfSaveDryRun_AndWritesTheFilterFile()
    {
        using var dir = new TempDir();
        var (iso, runner, paths) = Make(dir);
        var args = await iso.IsolateAsync("test", true, @"C:\e\winws.exe", ["--wf-tcp=443", "--filter-tcp=443", "--dpi-desync=fake", "--wf-save=old"], default);
        var dry = runner.Calls.Single().Args;
        Assert.Contains("--dry-run", dry);
        Assert.Contains("--wf-save=" + Path.Combine(paths.RunDir, "wfsave-test.txt"), dry);
        Assert.DoesNotContain("--wf-save=old", dry);
        Assert.Contains("--wf-tcp=443", dry);   // the saved filter is built from the strategy's own options
        string filterFile = Path.Combine(paths.RunDir, "filter-test.txt");
        Assert.Equal(["--filter-tcp=443", "--dpi-desync=fake", "--wf-raw=@" + filterFile], args);
        Assert.Equal(EngineIsolation.CandidateFilter(Saved, 40000, 40100), File.ReadAllText(filterFile));
    }

    [Fact]
    public async Task Isolate_FailsWhenDryRunFails_OrFilterTooLong()
    {
        using (var dir = new TempDir())
        {
            var (iso, _, _) = Make(dir, exitCode: 1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => iso.IsolateAsync("main", false, "w.exe", ["--wf-tcp=443"], default));
        }
        using (var dir = new TempDir())
        {
            var (iso, _, _) = Make(dir, saved: new string('x', EngineIsolation.MaxFilterChars));
            await Assert.ThrowsAsync<InvalidOperationException>(() => iso.IsolateAsync("main", false, "w.exe", ["--wf-tcp=443"], default));
        }
    }
}

public class EngineReadinessAndIsolationTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static EngineRestartPolicy Policy(double readySeconds = 3) =>
        new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), 5, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200),
            EngineRestartPolicy.WinwsReadyMarker, TimeSpan.FromSeconds(readySeconds));

    // "rem" swallows whatever arguments the service appends (the isolation filter)
    private static string[] Engine(string marker, int seconds) =>
        ["/c", $"echo {marker}& ping -n {seconds} 127.0.0.1 >nul& rem"];

    [Fact]
    public async Task ReadyMarker_MeansRunning()
    {
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(), Policy());
        var sw = Stopwatch.StartNew();
        var s = await engine.StartAsync("main", Cmd, Engine("windivert initialized", 20), default);
        Assert.True(s.Running);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "did not return on the marker");
    }

    [Fact]
    public async Task NoMarker_IsAFailedStart_AndTheProcessIsStopped()
    {
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(), Policy(readySeconds: 1));
        var s = await engine.StartAsync("main", Cmd, Engine("something else", 20), default);
        Assert.False(s.Running);
        Assert.False(engine.GetState("main").Running);
        Assert.Contains(log.Lines, l => l.Contains("no 'windivert initialized'"));
        Assert.Empty(engine.GetJobProcessIds("main"));
    }

    [Fact]
    public async Task ExitBeforeMarker_IsNotRunning()
    {
        await using var engine = new EngineControl(new MemoryLog(), new SystemClock(), Policy());
        var s = await engine.StartAsync("main", Cmd, ["/c", "exit 2"], default);
        Assert.False(s.Running);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TempDir Dir { get; } = new();
        public WindowsPaths Paths { get; }
        public FakeRunner Runner { get; }
        public MemoryLog Log { get; } = new();
        public EngineControl Engine { get; }
        public bool WfSaveWorks { get; set; } = true;

        /// <summary>Dynamic TCP port ranges the fake netsh reports; default = Windows default 49152-65535 twice.</summary>
        public Rig(IReadOnlyList<PortRange>? dynamicPorts = null, bool dynamicUnknown = false)
        {
            Paths = new WindowsPaths(Dir.Path, Dir.Path);
            Paths.EnsureDataDirs();
            Runner = new FakeRunner
            {
                Answer = (_, a) =>
                {
                    if (!WfSaveWorks)
                        return new ProcessResult(1, "", "unknown option --wf-save", false);
                    var save = a.First(x => x.StartsWith("--wf-save=", StringComparison.Ordinal));
                    File.WriteAllText(save["--wf-save=".Length..], "outbound and tcp.DstPort == 443");
                    return new ProcessResult(0, "", "", false);
                },
            };
            var ranges = new FakeDynamicPorts(dynamicUnknown ? null : dynamicPorts ?? [new(49152, 16384), new(49152, 16384)]);
            Engine = new EngineControl(Log, new SystemClock(), Policy(), new EngineIsolation(Runner, Paths, Log, dynamicPorts: ranges));
        }

        public int MainStarts => Log.Lines.Count(l => l.Contains("engine main: started"));
        public int MainStops => Log.Lines.Count(l => l.Contains("engine main: stopped"));

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Dir.Dispose();
        }
    }

    private sealed class FakeDynamicPorts(IReadOnlyList<PortRange>? ranges) : IDynamicPortRanges
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ranges);
        }
    }

    [Fact]
    public async Task DynamicPortsOverlap_MainIsIsolatedOnlyDuringTheTest()
    {
        // IPv6 range reaches into the check ports
        const bool unknown = false;
        await using var rig = new Rig([new(49152, 16384), new(40050, 25000)]);
        var e = rig.Engine;
        string[] mainArgs = [.. Engine("windivert initialized", 30), "--wf-tcp=443"];
        Assert.True((await e.StartAsync("main", Cmd, mainArgs, default)).Running);
        // main runs with its own arguments, the reason is in the journal
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine main: started") && l.Contains("--wf-tcp=443") && !l.Contains("--wf-raw"));
        Assert.Contains(rig.Log.Lines, l => l.Contains("dynamic TCP port range 40050-65049 overlaps the check ports 40000-40100"));
        Assert.False(unknown);

        int starts = rig.MainStarts;
        Assert.True((await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default)).Running);
        Assert.Equal(starts + 1, rig.MainStarts);   // isolated for the test
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine main: started") && l.Contains("filter-main.txt"));
        Assert.True((await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 30), "--wf-udp=443"], default)).Running);
        Assert.Equal(starts + 1, rig.MainStarts);   // not again for the next candidate

        await e.StopAsync("test", default);
        Assert.Equal(starts + 2, rig.MainStarts);   // back to its own arguments
        Assert.EndsWith("--wf-tcp=443", rig.Log.Lines.Last(l => l.Contains("engine main: started")));
        Assert.True(e.GetState("main").Running);
    }

    [Fact]
    public async Task DynamicPortsUnknown_AssumeDefault_MainPermanentlyIsolated()
    {
        await using var rig = new Rig(dynamicUnknown: true);
        var e = rig.Engine;
        Assert.True((await e.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default)).Running);
        Assert.Contains(rig.Log.Lines, l => l.Contains("dynamic TCP port ranges unknown, assuming the Windows default 49152-65535"));
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine main: started") && l.Contains("filter-main.txt"));
        int starts = rig.MainStarts, stops = rig.MainStops;
        await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default);
        await e.StopAsync("test", default);
        Assert.Equal(starts, rig.MainStarts);
        Assert.Equal(stops, rig.MainStops);
    }

    [Fact]
    public async Task CombinedSources_UnionAndUnknown()
    {
        var a = new FakeDynamicPorts([new(49152, 16384)]);
        var b = new FakeDynamicPorts([new(40050, 100)]);
        var none = new FakeDynamicPorts(null);
        Assert.Equal([new PortRange(49152, 16384), new PortRange(40050, 100)], await new CombinedDynamicPorts(a, none, b).GetTcpAsync(default));
        Assert.Null(await new CombinedDynamicPorts(none, none).GetTcpAsync(default));
    }

    [Fact]
    public async Task NoOverlap_IsDecidedOnce_AndLogged()
    {
        await using var rig = new Rig();
        await rig.Engine.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default);
        await rig.Engine.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default);
        Assert.Single(rig.Log.Lines, l => l.Contains("main always excludes local TCP ports 40000-40100 (dynamic TCP ports: 49152-65535, 49152-65535)"));
    }

    [Fact]
    public async Task TesterSequence_TestStaysUp_MainUntouched()
    {
        // the engine calls of Core Tester in mode isolated: pass-through "test" kept for the baseline, one "test" per
        // candidate (no StopAsync in between), StopAsync("test") in the restore
        await using var rig = new Rig();
        var e = rig.Engine;
        await e.StartAsync("main", Cmd, [.. Engine("windivert initialized", 60), "--wf-tcp=443"], default);
        int mainPid = e.GetState("main").Pid!.Value;
        int starts = rig.MainStarts, stops = rig.MainStops;
        string[][] calls = [["--wf-tcp=443", "--hostlist=guard"], ["--wf-tcp=443", "--dpi-desync=fake"], ["--wf-tcp=443", "--dpi-desync=multisplit"]];
        foreach (var c in calls)
        {
            var st = await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 60), .. c], default);
            Assert.True(st.Running);
            Assert.True(e.GetState("test").Running);   // up for the probes that follow
        }
        // "test" only goes down inside StartAsync while switching candidates, never between the calls
        Assert.Equal(calls.Length - 1, rig.Log.Lines.Count(l => l.Contains("engine test: stopped")));
        await e.StopAsync("test", default);
        Assert.False(e.GetState("test").Running);
        Assert.Equal(starts, rig.MainStarts);
        Assert.Equal(stops, rig.MainStops);
        Assert.Equal(mainPid, e.GetState("main").Pid);
    }

    [Fact]
    public async Task Main_IsAlwaysStartedWithTheExclusionFilter()
    {
        await using var rig = new Rig();
        Assert.True((await rig.Engine.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default)).Running);
        string filter = File.ReadAllText(Path.Combine(rig.Paths.RunDir, "filter-main.txt"));
        Assert.Equal(EngineIsolation.MainFilter("outbound and tcp.DstPort == 443", 40000, 40100), filter);
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine main: started") && l.Contains("--wf-raw=@") && l.Contains("filter-main.txt")
                                            && !l.Contains("--wf-tcp"));
    }

    [Fact]
    public async Task IsolatedTest_NeverStartsOrStopsMain()
    {
        await using var rig = new Rig();
        var e = rig.Engine;
        Assert.True((await e.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default)).Running);
        int mainPid = e.GetState("main").Pid!.Value;
        int starts = rig.MainStarts, stops = rig.MainStops;

        Assert.True((await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=80,443"], default)).Running);
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine test: started") && l.Contains("filter-test.txt") && !l.Contains("--wf-tcp"));
        // a second candidate, as the selection does for each strategy
        Assert.True((await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 30), "--wf-udp=443"], default)).Running);
        await e.StopAsync("test", default);

        Assert.Equal(starts, rig.MainStarts);
        Assert.Equal(stops, rig.MainStops);
        Assert.Equal(mainPid, e.GetState("main").Pid);
        Assert.True(e.GetState("main").Running);
    }

    [Fact]
    public async Task MainWithoutFilter_IsIsolatedOnceByTheFirstTest()
    {
        await using var rig = new Rig { WfSaveWorks = false };
        var e = rig.Engine;
        // no filter (e.g. an engine without --wf-save): main still runs, with its own arguments
        Assert.True((await e.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default)).Running);
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine main: started") && l.Contains("--wf-tcp=443") && !l.Contains("--wf-raw"));
        Assert.Contains(rig.Log.Lines, l => l.Contains("isolation filter failed"));

        rig.WfSaveWorks = true;
        int starts = rig.MainStarts;
        Assert.True((await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default)).Running);
        Assert.Equal(starts + 1, rig.MainStarts);   // one restart, now isolated
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine main: started") && l.Contains("filter-main.txt"));
        int isolatedPid = e.GetState("main").Pid!.Value;

        await e.StopAsync("test", default);
        await e.StartAsync("test", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443"], default);
        await e.StopAsync("test", default);
        Assert.Equal(starts + 1, rig.MainStarts);   // never again
        Assert.Equal(isolatedPid, e.GetState("main").Pid);
    }

    [Fact]
    public async Task Exclusive_CandidateRunsAsMain_WithTheExclusion()
    {
        // exclusive mode: the core stops "test" use and restarts "main" with the candidate's arguments
        await using var rig = new Rig();
        var e = rig.Engine;
        Assert.True((await e.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443", "--dpi-desync=fake"], default)).Running);
        Assert.True((await e.StartAsync("main", Cmd, [.. Engine("windivert initialized", 30), "--wf-tcp=443", "--dpi-desync=multisplit"], default)).Running);
        Assert.Contains(rig.Log.Lines, l => l.Contains("engine main: started") && l.Contains("--dpi-desync=multisplit") && l.Contains("filter-main.txt"));
        Assert.False(e.GetState("test").Running);
        // exclusive checks use ephemeral ports (49152+): the exclusion 40000–40100 does not hide them from main
        Assert.Contains("tcp.SrcPort < 40000 or tcp.SrcPort > 40100", File.ReadAllText(Path.Combine(rig.Paths.RunDir, "filter-main.txt")));
    }
}

public class ProbePortRetryTests
{
    [Fact]
    public async Task PortInTimeWait_IsSkipped()
    {
        await using var srv = new ScriptedServer(ScriptedServer.Respond(200, 10));
        // leave 40310 -> server in TIME_WAIT on our side: we close first
        using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            s.Bind(new IPEndPoint(IPAddress.Any, 40310));
            await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, srv.Port));
            // active close on our side, then wait for the server's FIN: our end goes to TIME_WAIT
            s.Shutdown(SocketShutdown.Send);
            while (await s.ReceiveAsync(new byte[256]) > 0)
            {
            }
        }
        await Task.Delay(200);
        var probe = new HttpProbe();
        var only = await probe.ProbeAsync(new ProbeRequest($"http://127.0.0.1:{srv.Port}/", 0, TimeSpan.FromSeconds(3), 40310, 40310), default);
        Assert.Equal("local_error", only.Error);   // the single port is blocked by TIME_WAIT
        var two = await probe.ProbeAsync(new ProbeRequest($"http://127.0.0.1:{srv.Port}/", 0, TimeSpan.FromSeconds(3), 40310, 40311), default);
        Assert.True(two.Ok, two.Error);
        Assert.Contains(40311, srv.ClientPorts);
    }
}

public class InstallAndNetworkTests
{
    [Fact]
    public void NetworkList_TakesNonDomainGuids()
    {
        var ids = NetworkList.Select(
        [
            new("{AAAAAAAA-1111-2222-3333-444444444444}", "Home", 1),
            new("{bbbbbbbb-1111-2222-3333-444444444444}", "Cafe", 0),
            new("{CCCCCCCC-1111-2222-3333-444444444444}", "corp.example", NetworkList.DomainCategory),
            new("not-a-guid", "x", 1),
        ]);
        Assert.Equal(["{AAAAAAAA-1111-2222-3333-444444444444}", "{BBBBBBBB-1111-2222-3333-444444444444}"], ids);
    }

    [Fact]
    public void InstallOptions_ToCalls()
    {
        var calls = InstallOptions.Calls(new InstallOptionValues(@"C:\x", "1.0.0", "1", "youtube, discord:full,"));
        Assert.Equal(["wizard.apply", "enable"], calls.Select(c => c.Method));
        Assert.Equal(["youtube", "discord:full"], calls[0].Args!["services"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(InstallOptions.Calls(new InstallOptionValues(null, null, "0", null)));
    }

    [Fact]
    public async Task InstallOptions_AreAppliedOnlyOnce()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, dir.Path);
        paths.EnsureDataDirs();
        var d = new FakeDispatcher();
        var o = new InstallOptionValues(null, "1.0.0", "1", "youtube");
        await InstallOptions.ApplyOnceAsync(d, paths, new MemoryLog(), o, default);
        await InstallOptions.ApplyOnceAsync(d, paths, new MemoryLog(), o, default);
        Assert.Equal(["wizard.apply", "enable"], d.Calls.Select(c => c.Method));
        Assert.All(d.Calls, c => Assert.True(c.Caller.IsAdmin));
        Assert.True(File.Exists(Path.Combine(paths.DataDir, InstallOptions.MarkerFile)));
    }
}
