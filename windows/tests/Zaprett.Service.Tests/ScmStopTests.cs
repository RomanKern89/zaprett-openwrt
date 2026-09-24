using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zaprett.Service.Hosting;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>Stop and shutdown from the SCM (reviews of 2026-09-23): a Windows shutdown killed the process with an
/// unhandled exception; a stop during the start ended Main with an exception; a hung stop left the process (and its
/// pipe) behind. The SCM side is <see cref="ScmConnection.OnControl"/> with the reported statuses captured.</summary>
[Collection(TimingGroup.Name)]
public class ScmStopTests
{
    private sealed class Statuses
    {
        public ConcurrentQueue<(uint State, uint Win32Exit, uint ServiceExit, uint WaitHint)> All { get; } = new();
        public void Report(uint state, uint win32, uint service, uint hint) => All.Enqueue((state, win32, service, hint));
        public uint? Last => All.IsEmpty ? null : All.Last().State;
        public int Stopped => All.Count(s => s.State == ScmConnection.StateStopped);
    }

    [Fact]
    public async Task Shutdown_WhileRunning_SignalsTheStop_ReportsStopped_NoExit()
    {
        int? exited = null;
        var seq = new StopSequence(c => exited = c, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300),
            exitDelay: TimeSpan.FromMilliseconds(100));
        var st = new Statuses();
        var svc = ScmConnection.CreateDetached(seq, st.Report);
        var sw = Stopwatch.StartNew();
        Assert.Equal(0u, svc.OnControl(ScmConnection.ControlShutdown));
        await svc.Pending!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(svc.StopRequested.IsCompleted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took {sw.Elapsed}");
        Assert.Equal(ScmConnection.StateStopped, st.Last);
        Assert.Equal(1, st.Stopped);
        // a shutdown never ends the process itself: Windows does, after its own limit
        Thread.Sleep(600);
        Assert.Null(exited);
    }

    [Fact]
    public async Task Stop_WhileRunning_ReportsPending_WaitsForTheHost_ThenStopped0()
    {
        int? exited = null;
        var seq = new StopSequence(c => exited = c, TimeSpan.FromSeconds(10));
        var st = new Statuses();
        var svc = ScmConnection.CreateDetached(seq, st.Report);
        _ = seq.StopRequested.ContinueWith(_ => { Thread.Sleep(200); seq.HostEnded(); }, TaskScheduler.Default);
        var sw = Stopwatch.StartNew();
        svc.OnControl(ScmConnection.ControlStop);
        await svc.Pending!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150), "did not wait for the host");
        var all = st.All.ToList();
        Assert.Equal(ScmConnection.StateStopPending, all[0].State);
        Assert.True(all[0].WaitHint > 0);
        Assert.Equal((ScmConnection.StateStopped, 0u, 0u), (all[^1].State, all[^1].Win32Exit, all[^1].ServiceExit));
        Assert.Null(exited);
        // a second STOP while stopping starts nothing more
        svc.OnControl(ScmConnection.ControlStop);
        Assert.Equal(1, st.Stopped);
    }

    [Fact]
    public void Controls_InterrogateAnswered_OthersNotImplemented_NoStop()
    {
        var st = new Statuses();
        var svc = ScmConnection.CreateDetached(new StopSequence(_ => { }), st.Report);
        Assert.Equal(0u, svc.OnControl(ScmConnection.ControlInterrogate));
        Assert.Equal(120u, svc.OnControl(2));   // PAUSE: not accepted
        Assert.False(svc.StopRequested.IsCompleted);
        Assert.Null(svc.Pending);
        Assert.Empty(st.All);
    }

    [Fact]
    public void Stop_WhenAskingForTimeFails_IsStillSignalled()
    {
        var seq = new StopSequence(_ => { }, TimeSpan.FromMilliseconds(100));
        seq.HostEnded();
        seq.Stop(() => throw new InvalidOperationException("UpdatePendingStatus can only be called during …"));
        Assert.True(seq.StopRequested.IsCompleted);
    }

    // a host that does not end is not left running after STOPPED: the process exits (its pipe would block the next start)
    [Fact]
    public void Stop_HostHangs_ProcessExitsAfterTheLimit()
    {
        int? exited = null;
        var seq = new StopSequence(c => exited = c, TimeSpan.FromMilliseconds(200), exitDelay: TimeSpan.FromMilliseconds(300));
        seq.Stop(() => { });
        Assert.Null(exited);   // not inside the stop: STOPPED goes out first
        Assert.True(SpinWait.SpinUntil(() => exited is not null, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, exited);
    }

    // B: STOPPED is reported before the process exits (an exit in STOP_PENDING looks like a crash to the SCM: 7034 and
    // a restart by the recovery actions)
    [Fact]
    public async Task Stop_HostHangs_StoppedIsReportedBeforeTheExit()
    {
        int? exited = null;
        uint? stateAtExit = null;
        var st = new Statuses();
        var seq = new StopSequence(c =>
        {
            stateAtExit = st.Last;
            exited = c;
        }, TimeSpan.FromMilliseconds(200), exitDelay: TimeSpan.FromMilliseconds(300));
        var svc = ScmConnection.CreateDetached(seq, st.Report);
        svc.OnControl(ScmConnection.ControlStop);
        await svc.Pending!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(exited);
        Assert.Equal(ScmConnection.StateStopped, st.Last);
        Assert.True(SpinWait.SpinUntil(() => exited is not null, TimeSpan.FromSeconds(5)));
        Assert.Equal(ScmConnection.StateStopped, stateAtExit);
    }

    // a host that ended by itself: STOPPED with its exit code (1066 + the code: the recovery actions apply), once
    [Theory]
    [InlineData(1, 1066u, 1u)]
    [InlineData(0, 0u, 0u)]
    public void HostEndsByItself_StoppedWithItsCode(int code, uint win32, uint service)
    {
        var st = new Statuses();
        var svc = ScmConnection.CreateDetached(new StopSequence(_ => { }), st.Report);
        svc.HostFinished(code);
        var s = Assert.Single(st.All);
        Assert.Equal((ScmConnection.StateStopped, win32, service), (s.State, s.Win32Exit, s.ServiceExit));
    }

    // 4th review: the host ends by itself and a STOP from the SCM arrives exactly between HostFinished's decision and
    // its report (forced here: the hook starts the STOP on another thread and lets it run into the handler). One
    // STOPPED with the host's code, nothing after it, no stop task started.
    [Fact]
    public void HostEnds_StopArrivesInTheWindow_OneStopped_NothingAfter()
    {
        var st = new Statuses();
        var svc = ScmConnection.CreateDetached(new StopSequence(_ => { }, TimeSpan.FromSeconds(5)), st.Report);
        uint? handlerResult = null;
        Thread? stop = null;
        svc.AfterStopDecision = () =>
        {
            stop = new Thread(() => handlerResult = svc.OnControl(ScmConnection.ControlStop));
            stop.Start();
            // the STOP gets every chance to slip in before the report
            Thread.Sleep(150);
        };
        svc.HostFinished(1);
        Assert.True(stop!.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(0u, handlerResult);
        svc.Pending?.Wait(TimeSpan.FromSeconds(5));
        var all = st.All.ToList();
        var only = Assert.Single(all);
        Assert.Equal((ScmConnection.StateStopped, 1066u, 1u), (only.State, only.Win32Exit, only.ServiceExit));
        Assert.Null(svc.Pending);
    }

    // STOPPED goes to the SCM once, however many ways the end is reached
    [Fact]
    public void Stopped_IsReportedOnce()
    {
        var st = new Statuses();
        var svc = ScmConnection.CreateDetached(new StopSequence(_ => { }), st.Report);
        svc.HostFinished(1);
        svc.HostFinished(1);
        Assert.Equal(0u, svc.OnControl(ScmConnection.ControlStop));
        Assert.Equal(1, st.Stopped);
    }

    // the stop is marked asked for inside the handler itself: a host ending at that moment does not report a failure
    [Fact]
    public void StopControl_MarksTheStopAtOnce()
    {
        var st = new Statuses();
        var seq = new StopSequence(_ => { }, TimeSpan.FromSeconds(5));
        var svc = ScmConnection.CreateDetached(seq, st.Report);
        svc.OnControl(ScmConnection.ControlStop);
        Assert.True(seq.StopRequested.IsCompleted);
        svc.HostFinished(1);
        Assert.DoesNotContain(st.All, s => s.Win32Exit != 0);
    }

    // a stop the SCM asked for ends with 0 whatever Environment.ExitCode holds, and STOPPED is reported once
    [Fact]
    public async Task AskedStop_ThenHostFinished_OneStopped0()
    {
        var st = new Statuses();
        var seq = new StopSequence(_ => { }, TimeSpan.FromSeconds(5));
        var svc = ScmConnection.CreateDetached(seq, st.Report);
        svc.OnControl(ScmConnection.ControlStop);
        await Task.Delay(100);
        svc.HostFinished(1);
        await svc.Pending!.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = Assert.Single(st.All, s => s.State == ScmConnection.StateStopped);
        Assert.Equal(0u, stopped.Win32Exit);
    }

    // at shutdown Windows grants a few seconds: the wait is bounded and nothing exits the process early
    [Fact]
    public void Shutdown_HostHangs_ReturnsWithinItsLimit()
    {
        int? exited = null;
        var seq = new StopSequence(c => exited = c, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        seq.Shutdown();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"shutdown waited {sw.Elapsed}");
        Assert.True(seq.StopRequested.IsCompleted);
        Assert.Null(exited);
    }

    private sealed class SlowStart(TimeSpan delay) : IHostedService
    {
        public bool Stopped;
        public Task StartAsync(CancellationToken ct) => Task.Delay(delay, ct);
        public Task StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    // A: a service whose stop fails: past the host's ShutdownTimeout (OperationCanceledException) or with an error
    private sealed class FailingStop(Exception error) : IHostedService
    {
        public int Stops;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Stops);
            return Task.FromException(error);
        }
    }

    private static IHost Build(params IHostedService[] services)
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        b.Services.AddSingleton<IHostLifetime, ScmHostLifetime>();
        foreach (var s in services)
            b.Services.AddSingleton(s);
        return b.Build();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AskedStop_FailingToStop_IsOneStop_Code0(bool timeout)
    {
        var failing = new FailingStop(timeout ? new OperationCanceledException("shutdown timeout") : new InvalidOperationException("cannot stop"));
        var stop = new TaskCompletionSource();
        var log = new MemoryLog();
        using var host = Build(failing);
        var run = HostRun.RunAsync(host, stop.Task, log);
        await Task.Delay(300);
        stop.SetResult();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, failing.Stops);
        Assert.Contains(log.Lines, l => l.Contains("service: the stop ended with"));
    }

    [Fact]
    public async Task StopBeforeTheStart_IsCode0_WithoutException()
    {
        var stop = new TaskCompletionSource();
        stop.SetResult();
        using var host = Build(new SlowStart(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, await HostRun.RunAsync(host, stop.Task).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task StopDuringTheStart_IsCode0_AndStartedServicesStop()
    {
        var stop = new TaskCompletionSource();
        var started = new SlowStart(TimeSpan.Zero);
        var slow = new SlowStart(TimeSpan.FromSeconds(5));
        using var host = Build(started, slow);
        var run = HostRun.RunAsync(host, stop.Task);
        await Task.Delay(300);
        stop.SetResult();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(started.Stopped, "a service that had started was not stopped");
    }

    // negative control: what the plain RunAsync did in Program.cs (the exception left Main, the SCM saw code 1)
    [Fact]
    public async Task PlainRunAsync_ThrowsOnAStopDuringTheStart()
    {
        using var host = Build(new SlowStart(TimeSpan.FromSeconds(5)));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var run = host.RunAsync();
        await Task.Delay(300);
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task StopWhileRunning_IsCode0()
    {
        var stop = new TaskCompletionSource();
        var slow = new SlowStart(TimeSpan.Zero);
        using var host = Build(slow);
        var run = HostRun.RunAsync(host, stop.Task);
        await Task.Delay(300);
        stop.SetResult();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(slow.Stopped);
    }

    // ports: a broken source does not lose the other's answer; a source left behind gets the cancellation
    private sealed class Throws : IDynamicPortRanges
    {
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct) => throw new InvalidCastException("Specified cast is not valid.");
    }

    private sealed class Good : IDynamicPortRanges
    {
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PortRange>?>([new PortRange(49152, 16384)]);
    }

    private sealed class Abandoned : IDynamicPortRanges
    {
        public CancellationToken Token;
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct)
        {
            Token = ct;
            return new TaskCompletionSource<IReadOnlyList<PortRange>?>().Task;
        }
    }

    [Fact]
    public async Task Ports_ABrokenSource_TheOtherCounts()
    {
        var log = new MemoryLog();
        var r = await new CombinedDynamicPorts(TimeSpan.FromSeconds(3), log, new Throws(), new Good()).GetTcpAsync(default);
        Assert.Equal([new PortRange(49152, 16384)], r);
        Assert.Contains(log.Lines, l => l.Contains("dynamic ports: Throws failed: InvalidCastException"));
    }

    [Fact]
    public async Task Ports_ASourceLeftBehind_IsCancelled()
    {
        var abandoned = new Abandoned();
        var time = new ManualTime();
        var ask = new CombinedDynamicPorts(TimeSpan.FromSeconds(3), null, time, abandoned, new Good()).GetTcpAsync(default);
        // timers: [0] the limit of the whole query, [1] WaitAsync of the abandoned source; only the second one fires,
        // as when WaitAsync wins the race against the limit's timer
        Assert.True(SpinWait.SpinUntil(() => time.Count >= 2, TimeSpan.FromSeconds(5)), $"timers: {time.Count}");
        time.Fire(1);
        await ask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(abandoned.Token.IsCancellationRequested);
    }

    internal sealed class ManualTime : TimeProvider
    {
        private readonly List<Timer> _timers = [];

        public int Count
        {
            get
            {
                lock (_timers)
                    return _timers.Count;
            }
        }

        public void FireAll()
        {
            List<Timer> all;
            lock (_timers)
                all = [.. _timers];
            foreach (var t in all)
                t.Fire();
        }

        public void Fire(int index)
        {
            Timer t;
            lock (_timers)
                t = _timers[index];
            t.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new Timer(callback, state);
            lock (_timers)
                _timers.Add(t);
            return t;
        }

        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire()
            {
                if (!_disposed)
                    callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Decision_ABrokenSource_IsTheDefault_AndStaysUsable()
    {
        using var dir = new TempDir();
        var iso = new EngineIsolation(new FakeRunner(), new WindowsPaths(dir.Path, dir.Path), new MemoryLog(), dynamicPorts: new Throws());
        Assert.True(await iso.PermanentMainIsolationAsync(default));
        Assert.True(await iso.PermanentMainIsolationAsync(default));
    }
}
