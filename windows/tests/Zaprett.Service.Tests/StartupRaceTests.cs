using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zaprett.Core;
using Zaprett.Core.Platform;
using Zaprett.Service.Hosting;
using Zaprett.Service.Ipc;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>
/// Service start (the Windows 11 test machine, 2026-09-23): the core's startup started the engine while the scheduler's first watchdog
/// ("ensure") ran at the same time, saw no engine, started it again (stop + start) and logged a false warning.
/// This host runs the real IpcHostService and SchedulerService with a core that behaves like Zaprett.Core:
/// startup takes a while and then starts "main"; "ensure" starts "main" only when it is not running.
/// </summary>
public class StartupRaceTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private sealed class SlowCore(EngineControl engine, ILog log) : ICommandDispatcher
    {
        public IReadOnlySet<string> ReadOnlyMethods { get; } = new HashSet<string> { "status" };
        public event Action<string, JsonObject>? Event;
        public int EnsureCalls;

        private static readonly string[] Args = ["/c", "echo windivert initialized& ping -n 60 127.0.0.1 >nul& rem"];

        // like CommandDispatcher.StartupAsync: config, generation, dry-run — then the engine
        public async Task<JsonObject> StartupAsync(CancellationToken ct)
        {
            await Task.Delay(1500, ct);
            var st = await engine.StartAsync("main", Cmd, Args, ct);
            Event?.Invoke("status", new JsonObject());
            return new JsonObject { ["ok"] = true, ["running"] = st.Running };
        }

        public async Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct)
        {
            if (method != "ensure")
                return new JsonObject { ["ok"] = true };
            Interlocked.Increment(ref EnsureCalls);
            if (!engine.GetState("main").Running)
            {
                var st = await engine.StartAsync("main", Cmd, Args, ct);
                log.Warn($"сторож: движок не работал — запущен заново (pid {st.Pid})");
            }
            return new JsonObject { ["ok"] = true };
        }
    }

    // D5-bis (the Windows 11 test machine, 21:27): "enable" was starting main (isolation filter, launch, ready line) when the watchdog tick
    // came; it saw no running engine and logged "was not running and has been started again".
    [Fact]
    public async Task WatchdogDuringAStart_SeesItAsRunning()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, dir.Path);
        paths.EnsureDataDirs();
        var log = new MemoryLog();
        var runner = new FakeRunner
        {
            Answer = (_, a) =>
            {
                // --wf-save --dry-run of the isolation filter takes a while, as winws does
                Thread.Sleep(800);
                File.WriteAllText(a.First(x => x.StartsWith("--wf-save=", StringComparison.Ordinal))["--wf-save=".Length..], "tcp");
                return new ProcessResult(0, "", "", false);
            },
        };
        var engine = new EngineControl(log, new SystemClock(),
            EngineRestartPolicy.Winws with { RestartDelay = TimeSpan.FromSeconds(30) },
            new EngineIsolation(runner, paths, log, dynamicPorts: new NoOverlap()));
        await using (engine)
        {
            var core = new SlowCore(engine, log);
            var enable = Task.Run(() => engine.StartAsync("main", Cmd, ["/c", "echo windivert initialized& ping -n 60 127.0.0.1 >nul& rem"], default));
            // the watchdog ticks in the middle of the start
            await Task.Delay(300);
            Assert.True(engine.GetState("main").Running, "a start in progress must count as running");
            Assert.Equal(Zaprett.Core.Platform.EnginePhases.Starting, engine.GetState("main").Phase);
            await core.InvokeAsync("ensure", null, Zaprett.Core.CallerInfo.System, default);
            await enable;
            Assert.DoesNotContain(log.Lines, l => l.Contains("сторож"));
            Assert.Single(log.Lines, l => l.Contains("engine main: started"));
            Assert.Equal(Zaprett.Core.Platform.EnginePhases.Capturing, engine.GetState("main").Phase);
        }
    }

    // negative control: an engine that really died is restarted by the watchdog, with the warning
    [Fact]
    public async Task WatchdogAfterACrash_StartsAgainAndWarns()
    {
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(),
            EngineRestartPolicy.Winws with { RestartDelay = TimeSpan.FromMinutes(5) });
        var core = new SlowCore(engine, log);
        await engine.StartAsync("main", Cmd, ["/c", "echo windivert initialized& ping -n 2 127.0.0.1 >nul"], default);
        Assert.True(await Wait.UntilAsync(() => !engine.GetState("main").Running, TimeSpan.FromSeconds(10)), "engine did not exit");
        await core.InvokeAsync("ensure", null, Zaprett.Core.CallerInfo.System, default);
        Assert.Contains(log.Lines, l => l.Contains("сторож: движок не работал"));
        Assert.True(engine.GetState("main").Running);
    }

    // StartAsync reports what it launched, not its own "starting" mark: a dead start is not running, a live one is capturing
    [Fact]
    public async Task StartResult_IsTheRealState_WithoutReadyMarker()
    {
        await using var engine = new EngineControl(new MemoryLog(), new SystemClock(), new EngineRestartPolicy(TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(10), 5, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500), null, null));
        var dead = await engine.StartAsync("main", Cmd, ["/c", "exit 2"], default);
        Assert.False(dead.Running);
        var alive = await engine.StartAsync("main", Cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], default);
        Assert.True(alive.Running);
        Assert.Equal(Zaprett.Core.Platform.EnginePhases.Capturing, alive.Phase);
    }

    private sealed class NoOverlap : IDynamicPortRanges
    {
        public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PortRange>?>([new PortRange(49152, 16384)]);
    }

    [Fact]
    public async Task ServiceStart_StartsMainOnce_WatchdogSeesItRunning()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, dir.Path);
        paths.EnsureDataDirs();
        File.WriteAllText(paths.ConfigFile, """{"main":{"enabled":true,"watchdog":true},"monitor":{"enabled":false}}""");
        var log = new MemoryLog();
        var clock = new SystemClock();
        var engine = new EngineControl(log, clock, new EngineRestartPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), 5,
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200), EngineRestartPolicy.WinwsReadyMarker, TimeSpan.FromSeconds(5)));
        var core = new SlowCore(engine, log);
        var runner = new FakeRunner();
        var platform = new PlatformServices(paths, clock, runner, engine, new HttpProbe(), new DnsResolver(),
            new DnsControl(runner, paths, log, osBuild: 19045), new FirewallControl(runner, log), new ConflictScanner(paths, log),
            new SystemInfo(paths, log), log);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = dir.Path });
        builder.Services.AddSingleton(platform);
        builder.Services.AddSingleton(new ServiceMode(false));
        builder.Services.AddSingleton<ICommandDispatcher>(core);
        builder.Services.AddSingleton(new CoreStartup(core.StartupAsync));
        builder.Services.AddSingleton(new StartupGate());
        builder.Services.AddSingleton(new PipeServerOptions { PipeName = "zaprett-test-" + Guid.NewGuid().ToString("N") });
        builder.Services.AddHostedService<IpcHostService>();
        builder.Services.AddHostedService<SchedulerService>();
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref core.EnsureCalls) >= 1, TimeSpan.FromSeconds(15)), "watchdog never ran");
            await Task.Delay(500);
            Assert.Single(log.Lines, l => l.Contains("engine main: started"));
            Assert.DoesNotContain(log.Lines, l => l.Contains("engine main: stopped"));
            Assert.DoesNotContain(log.Lines, l => l.Contains("сторож"));
            Assert.Contains(log.Lines, l => l.Contains("core startup (") && l.EndsWith(" s): {\"ok\":true,\"running\":true}", StringComparison.Ordinal));
            Assert.True(engine.GetState("main").Running);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
