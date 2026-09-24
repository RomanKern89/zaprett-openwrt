using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Text.Json.Nodes;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

public class ProcessTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>PIDs of ping.exe processes whose command line carries a marker count.</summary>
    private static List<int> PingsWith(string marker)
    {
        using var s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'PING.EXE'");
        return s.Get().Cast<ManagementObject>()
            .Where(o => (o["CommandLine"] as string ?? "").Contains(marker, StringComparison.Ordinal))
            .Select(o => (int)(uint)o["ProcessId"]).ToList();
    }

    [Fact]
    public async Task Runner_CapturesOutputAndExitCode()
    {
        var r = await new ProcessRunner().RunAsync(Cmd, ["/c", "echo hello& echo oops 1>&2& exit 7"], TimeSpan.FromSeconds(20), default);
        Assert.False(r.TimedOut);
        Assert.Equal(7, r.ExitCode);
        Assert.Equal("hello", r.StdOut.Trim());
        Assert.Equal("oops", r.StdErr.Trim());
    }

    [Fact]
    public async Task Runner_DecodesOemOutput()
    {
        // cmd prints in the OEM code page (cp866 on a Russian system); UTF-8 would fail and fall back
        var r = await new ProcessRunner().RunAsync(Cmd, ["/c", "echo привет мир"], TimeSpan.FromSeconds(20), default);
        Assert.Equal("привет мир", r.StdOut.Trim());
    }

    [Fact]
    public async Task Runner_TimeoutKillsTheWholeTree()
    {
        string marker = "-n 41";
        var sw = Stopwatch.StartNew();
        var task = new ProcessRunner().RunAsync(Cmd, ["/c", $"ping {marker} 127.0.0.1"], TimeSpan.FromSeconds(2), default);
        Assert.True(await Wait.UntilAsync(() => PingsWith(marker).Count > 0, TimeSpan.FromSeconds(10)), "ping never started");
        var r = await task;
        Assert.True(r.TimedOut);
        Assert.InRange(sw.ElapsedMilliseconds, 1500, 15000);   // not the 40 s ping would take
        Assert.True(await Wait.UntilAsync(() => PingsWith(marker).Count == 0, TimeSpan.FromSeconds(5)), "grandchild survived");
    }

    [Fact]
    public async Task ClosingTheJob_KillsTheTree_WithoutExplicitKill()
    {
        string marker = "-n 47";
        var proc = Win32Process.Start(Cmd, ["/c", $"ping {marker} 127.0.0.1"]);
        Assert.True(await Wait.UntilAsync(() => PingsWith(marker).Count > 0, TimeSpan.FromSeconds(10)), "ping never started");
        int pid = proc.Pid;
        proc.Dispose();   // KILL_ON_JOB_CLOSE: the last job handle closes
        Assert.True(await Wait.UntilAsync(() => PingsWith(marker).Count == 0 && !IsAlive(pid), TimeSpan.FromSeconds(5)), "tree survived the job");
    }

    [Fact]
    public async Task Runner_MissingProgram_Throws()
    {
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            new ProcessRunner().RunAsync(@"C:\definitely\missing.exe", [], TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task Engine_StopKillsEngineAndItsChildren()
    {
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(),
            new EngineRestartPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10), 5, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(300)));
        // unique per run: the same test in another dotnet test on this machine (parallel sessions) starts the same ping
        string marker = $"-w {Random.Shared.Next(1000, 60000)}";
        var state = await engine.StartAsync("main", Cmd, ["/c", $"ping -n 43 {marker} 127.0.0.1"], default);
        Assert.True(state.Running);
        Assert.NotNull(state.Pid);
        // cmd + its conhost make two processes before ping exists: wait for the ping itself to be in the job (the count
        // alone let the snapshot be taken before ping started — "Not found: 42240" under a loaded full test run)
        Assert.True(await Wait.UntilAsync(() => PingsWith(marker).Any(p => engine.GetJobProcessIds("main").Contains(p)),
            TimeSpan.FromSeconds(20)), "the ping of the engine never showed up in its job");
        var pids = engine.GetJobProcessIds("main");
        Assert.Contains(state.Pid!.Value, pids);
        Assert.Contains(PingsWith(marker), p => pids.Contains(p));

        await engine.StopAsync("main", default);
        Assert.False(engine.GetState("main").Running);
        Assert.True(await Wait.UntilAsync(() => pids.All(p => !IsAlive(p)), TimeSpan.FromSeconds(5)), "a process of the job survived");
        Assert.Contains(log.Lines, l => l.Contains("engine main: stopped"));
    }

    [Fact]
    public async Task Engine_OutputGoesToTheLog()
    {
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(),
            new EngineRestartPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), 5, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(300)));
        await engine.StartAsync("test", Cmd, ["/c", "echo engine-says-hi& echo engine-err 1>&2& ping -n 5 127.0.0.1 >nul"], default);
        Assert.True(await Wait.UntilAsync(() => log.Lines.Any(l => l.Contains("engine test [out]: engine-says-hi")) &&
                                                log.Lines.Any(l => l.Contains("engine test [err]: engine-err")), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Engine_CrashLoop_RestartsThenPauses()
    {
        var log = new MemoryLog();
        var events = new ConcurrentQueue<string>();
        await using var engine = new EngineControl(log, new SystemClock(),
            new EngineRestartPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10), 3, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(200)));
        engine.Event += (type, data) => events.Enqueue(type + ":" + data["engine"]!["state"]!.GetValue<string>() + ":" +
                                                      data["engine"]!["restarts"]!.GetValue<int>());
        await engine.StartAsync("main", Cmd, ["/c", "exit 3"], default);
        Assert.True(await Wait.UntilAsync(() => events.Any(e => e.StartsWith("status:paused", StringComparison.Ordinal)), TimeSpan.FromSeconds(15)));
        var list = events.ToList();
        Assert.Equal("status:crashed:1", list[0]);
        Assert.Equal("status:restarted:1", list[1]);
        Assert.Equal("status:crashed:2", list[2]);
        Assert.Equal("status:restarted:2", list[3]);
        Assert.Equal("status:paused:3", list[4]);
        // during the pause nothing is restarted
        await Task.Delay(500);
        Assert.Equal(5, events.Count);
        Assert.False(engine.GetState("main").Running);
        Assert.Equal(3, engine.GetState("main").RestartsInWindow);
        Assert.Contains(log.Lines, l => l.Contains("exited with code 3"));
    }

    [Fact]
    public async Task Engine_MissingExecutable_IsNotRunning()
    {
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock());
        var s = await engine.StartAsync("main", @"C:\missing\winws.exe", ["--dry-run"], default);
        Assert.False(s.Running);
        Assert.Contains(log.Lines, l => l.StartsWith("ERROR engine main: cannot start", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync("Main!", Cmd, [], default));
    }
}
