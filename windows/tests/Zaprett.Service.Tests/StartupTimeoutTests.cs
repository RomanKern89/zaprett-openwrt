using System.Diagnostics;
using Zaprett.Core.Platform;
using Zaprett.Service.Hosting;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>Timing tests run alone: in the parallel run the thread pool is busy with blocking tests, and a 1.5 s timer
/// fired after 5.7 s (23.09.2026, machine loaded by other builds).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingGroup
{
    public const string Name = "timing";
}

/// <summary>D13: after a fresh install WMI answered the dynamic port ranges in 19.4 s and the engine start waited for
/// it; the service process reached the SCM too late (7009/7000). Sources get a time limit, the decision too.</summary>
[Collection(TimingGroup.Name)]
public class StartupTimeoutTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private sealed class Ranges(params PortRange[] ranges) : IDynamicPortRanges
    {
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PortRange>?>(ranges);
    }

    // a hung WMI call: ignores the cancellation
    private sealed class Hung : IDynamicPortRanges
    {
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct) => new TaskCompletionSource<IReadOnlyList<PortRange>?>().Task;
    }

    // a slow netsh: honours the cancellation
    private sealed class Slow : IDynamicPortRanges
    {
        public async Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return [new PortRange(40000, 100)];
        }
    }

    [Fact]
    public async Task Combined_AHungSource_IsLeftBehind_TheOtherCounts()
    {
        var log = new MemoryLog();
        var sw = Stopwatch.StartNew();
        var r = await new CombinedDynamicPorts(TimeSpan.FromMilliseconds(300), log, new Hung(), new Ranges(new PortRange(49152, 16384)))
            .GetTcpAsync(default);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"waited {sw.Elapsed}");
        Assert.Equal([new PortRange(49152, 16384)], r);
        Assert.Contains(log.Lines, l => l.Contains("Hung did not answer within 0.3 s") || l.Contains("Hung did not answer within 0,3 s"));
    }

    private static bool Warned(MemoryLog log) => log.Lines.Any(l => l.StartsWith("WARN", StringComparison.Ordinal));

    // one start of the service against the same history file: first source answers or hangs, the second as asked
    private static async Task<MemoryLog> StartOnce(string history, bool firstAnswers, bool secondAnswers, bool osBoot)
    {
        var log = new MemoryLog();
        IDynamicPortRanges second = secondAnswers ? new Ranges(new PortRange(49152, 16384)) : new Hung();
        await new CombinedDynamicPorts(TimeSpan.FromMilliseconds(200), log, new Toggle(firstAnswers), second)
        {
            HistoryFile = history,
            OsBoot = osBoot,
        }.GetTcpAsync(default);
        return log;
    }

    // Win10 cold starts: netsh misses its 3 s at every start, WMI answers. Something to use was there: Info, always
    [Fact]
    public async Task OneSourceFails_TheOtherAnswers_InfoEvenWhenItRepeats()
    {
        using var dir = new TempDir();
        string history = Path.Combine(dir.Path, "dynamic-ports.json");
        foreach (bool boot in new[] { true, false, false, false })
        {
            var log = await StartOnce(history, firstAnswers: false, secondAnswers: true, osBoot: boot);
            Assert.False(Warned(log), string.Join("\n", log.Lines));
            // the line still names the source and its time limit
            Assert.Contains(log.Lines, l => l.StartsWith("INFO dynamic ports: Toggle did not answer within 0", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Lines, l => l.Contains("no source answered"));
        }
    }

    // nothing to use (the Windows default assumed) twice in a row, not after a boot: WARN naming the sources and limits
    [Fact]
    public async Task BothFail_TwiceNotAtBoot_Warn()
    {
        using var dir = new TempDir();
        string history = Path.Combine(dir.Path, "dynamic-ports.json");
        var first = await StartOnce(history, false, false, osBoot: false);
        Assert.False(Warned(first));
        Assert.Contains(first.Lines, l => l.StartsWith("INFO dynamic ports: no source answered", StringComparison.Ordinal));
        var second = await StartOnce(history, false, false, osBoot: false);
        var warn = Assert.Single(second.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal));
        Assert.Contains("no source answered (Toggle did not answer within 0", warn);
        Assert.Contains("Hung did not answer within 0", warn);
        Assert.EndsWith("the Windows default is assumed; also at the previous service start", warn);
    }

    // after a boot the sources are always slow: a failure at a boot start, even after the same at the last boot, is Info;
    // the boot only exempts itself: the same at the next (non-boot) start is a WARN
    [Fact]
    public async Task BothFail_AtBootAfterTheSame_Info_NextRestartWarns()
    {
        using var dir = new TempDir();
        string history = Path.Combine(dir.Path, "dynamic-ports.json");
        Assert.False(Warned(await StartOnce(history, false, false, osBoot: true)));
        Assert.False(Warned(await StartOnce(history, false, false, osBoot: true)));
        Assert.True(Warned(await StartOnce(history, false, false, osBoot: false)));
    }

    // negative control: an answer in between resets it — the next failure is only Info, the one after that warns again
    [Fact]
    public async Task AnAnswerInBetween_StartsOver()
    {
        using var dir = new TempDir();
        string history = Path.Combine(dir.Path, "dynamic-ports.json");
        await StartOnce(history, false, false, osBoot: false);
        await StartOnce(history, true, false, osBoot: false);
        Assert.False(Warned(await StartOnce(history, false, false, osBoot: false)));
        Assert.True(Warned(await StartOnce(history, false, false, osBoot: false)));
    }

    // one source class that answers or hangs, so the history (kept per source name) sees the same name
    private sealed class Toggle(bool answers) : IDynamicPortRanges
    {
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct) => answers
            ? Task.FromResult<IReadOnlyList<PortRange>?>([new PortRange(49152, 16384)])
            : new TaskCompletionSource<IReadOnlyList<PortRange>?>().Task;
    }

    // negative control: without a history every failure is Info (nothing to compare with)
    [Fact]
    public async Task SlowSource_NoHistory_AlwaysInfo()
    {
        for (int i = 0; i < 2; i++)
        {
            var log = new MemoryLog();
            await new CombinedDynamicPorts(TimeSpan.FromMilliseconds(200), log, new Hung()).GetTcpAsync(default);
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Combined_AllSlow_IsUnknown_Quickly()
    {
        var sw = Stopwatch.StartNew();
        var r = await new CombinedDynamicPorts(TimeSpan.FromMilliseconds(300), null, new Hung(), new Slow()).GetTcpAsync(default);
        Assert.Null(r);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"waited {sw.Elapsed}");
    }

    // the sources are asked at the same time: two slow ones cost one time limit, not two
    [Fact]
    public async Task Combined_AsksTheSourcesTogether()
    {
        // no timer fires until the test says so: asked one after another, only the first source would be asked
        // (a wall clock check failed on a machine loaded by other builds: a 1.5 s timer fired after 5 s)
        var time = new ScmStopTests.ManualTime();
        var sources = new[] { new Counting(), new Counting(), new Counting() };
        var ask = new CombinedDynamicPorts(TimeSpan.FromSeconds(3), null, time, sources).GetTcpAsync(default);
        Assert.True(SpinWait.SpinUntil(() => sources.All(s => s.Asked), TimeSpan.FromSeconds(5)),
            $"asked: {sources.Count(s => s.Asked)} of 3 before any time passed");
        time.FireAll();
        Assert.Null(await ask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Counting : IDynamicPortRanges
    {
        public volatile bool Asked;
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct)
        {
            Asked = true;
            return new TaskCompletionSource<IReadOnlyList<PortRange>?>().Task;
        }
    }

    // the caller's own cancellation is not turned into "unknown"
    [Fact]
    public async Task Combined_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CombinedDynamicPorts(TimeSpan.FromSeconds(10), null, new Hung()).GetTcpAsync(cts.Token));
    }

    [Fact]
    public async Task Decision_WithAHungSource_AssumesTheDefault_WithinTheLimit()
    {
        using var dir = new TempDir();
        var log = new MemoryLog();
        var iso = new EngineIsolation(new FakeRunner(), new WindowsPaths(dir.Path, dir.Path), log, dynamicPorts: new Hung(),
            portsTimeout: TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        Assert.True(await iso.PermanentMainIsolationAsync(default));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"waited {sw.Elapsed}");
        Assert.Contains(log.Lines, l => l.Contains("were not read within"));
        Assert.Contains(log.Lines, l => l.Contains("ranges unknown, assuming the Windows default"));
    }

    // negative control: an answer within the limit decides (an overlap turns permanent isolation off)
    [Fact]
    public async Task Decision_WithAnAnswer_UsesIt()
    {
        using var dir = new TempDir();
        var log = new MemoryLog();
        var iso = new EngineIsolation(new FakeRunner(), new WindowsPaths(dir.Path, dir.Path), log,
            dynamicPorts: new Ranges(new PortRange(10000, 55535)), portsTimeout: TimeSpan.FromMilliseconds(300));
        Assert.False(await iso.PermanentMainIsolationAsync(default));
        Assert.DoesNotContain(log.Lines, l => l.Contains("were not read within"));
    }

    // the engine start is not held by a hung source longer than the limit
    [Fact]
    public async Task EngineStart_IsNotHeldByAHungSource()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, dir.Path);
        paths.EnsureDataDirs();
        var runner = new FakeRunner
        {
            Answer = (_, a) =>
            {
                File.WriteAllText(a.First(x => x.StartsWith("--wf-save=", StringComparison.Ordinal))["--wf-save=".Length..], "tcp");
                return new ProcessResult(0, "", "", false);
            },
        };
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(), EngineRestartPolicy.Winws with { RestartDelay = TimeSpan.FromSeconds(30) },
            new EngineIsolation(runner, paths, log, dynamicPorts: new Hung(), portsTimeout: TimeSpan.FromMilliseconds(500)));
        var sw = Stopwatch.StartNew();
        var st = await engine.StartAsync("main", Cmd, ["/c", "echo windivert initialized& ping -n 30 127.0.0.1 >nul& rem"], default);
        Assert.True(st.Running);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"start took {sw.Elapsed}");
    }

    // the test host is not a service: the dispatcher refuses at once and the program runs without the SCM
    [Fact]
    public void ScmConnection_OutsideTheScm_IsNull_AtOnce()
    {
        var sw = Stopwatch.StartNew();
        Assert.Null(ScmConnection.Connect());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}
