using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zaprett.Core;
using Zaprett.Core.Platform;
using Zaprett.Ipc;
using Zaprett.Service.Hosting;
using Zaprett.Service.Ipc;
using Zaprett.Service.Platform;

namespace Zaprett.Service;

// zaprett-svc: Windows service "zaprett" (ARCHITECTURE-WIN §2 W-2).
//   zaprett-svc                 run under the service control manager
//   zaprett-svc --console       run in this console (development); Ctrl+C stops
//   options: --stub (no core, stub dispatcher), --pipe <name> (or ZAPRETT_PIPE; default "zaprett")
//   ZAPRETT_INSTALL_DIR / ZAPRETT_DATA_DIR override the Program Files / ProgramData locations.
internal static class Program
{
    // Main stays this small: the JIT loads the assemblies of every type a method mentions before it runs, and under a
    // Defender scan of a fresh install each load costs time the SCM does not wait for (D13). Nothing of the host, the
    // core or the platform is touched before the SCM handshake.
    private static int Main(string[] args)
    {
        var mainAt = DateTime.Now;
        // installer: load the files ahead of the first service start, then exit (never the SCM, never ProgramData)
        if (Array.IndexOf(args, "--warmup") >= 0)
            return RunWarmup();
        bool console = Array.IndexOf(args, "--console") >= 0;
        var scm = console ? null : ScmConnection.Connect();
        // started by the SCM (parent services.exe) and still no handshake: not a console program under LocalSystem
        bool orphan = scm is null && !console && StartedByScm();
        int code;
        try
        {
            code = RunAsync(args, mainAt, scm, orphan).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // not out of Main: the SCM gets STOPPED with a failure code, and the reason is in the journal
            code = 1;
            ReportFailure(e);
        }
        scm?.HostFinished(code);
        return code;
    }

    // separate methods: their assemblies (Hosting.WindowsServices, Zaprett.Core) load only when they run
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool StartedByScm()
    {
        try
        {
            return Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
        }
        catch (Exception e)
        {
            // the parent process could not be read: not taken for an SCM start (the program keeps running)
            Console.Error.WriteLine("zaprett-svc: cannot tell whether the SCM started this process: " + e.Message);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReportFailure(Exception e) => FailureLog.Report(e);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWarmup() => Warmup.Run(Console.Out);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunAsync(string[] args, DateTime mainAt, ScmConnection? scm, bool orphan)
    {
        bool console = args.Contains("--console");
        bool asSystem;
        using (var self = System.Security.Principal.WindowsIdentity.GetCurrent())
            asSystem = self.IsSystem;
        bool stub = args.Contains("--stub");
        string pipeName = Environment.GetEnvironmentVariable("ZAPRETT_PIPE") is { Length: > 0 } envPipe ? envPipe : ZaprettPipeClient.DefaultPipeName;
        int pipeArg = Array.IndexOf(args, "--pipe");
        if (pipeArg >= 0 && pipeArg + 1 < args.Length)
            pipeName = args[pipeArg + 1];
        if (!JsonRpc.IsValidMethodName(pipeName))
        {
            Console.Error.WriteLine("invalid pipe name: " + pipeName);
            return 2;
        }

        var paths = WindowsPaths.FromEnvironment();
        var dataDirTime = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // as SYSTEM (the real service) the data directory is checked and locked down; in console mode it is the developer's
            if (asSystem)
                DataDirSecurity.Secure(paths);
            else
                paths.EnsureDataDirs();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("zaprett-svc: data directory: " + e.Message);
            return 1;
        }
        dataDirTime.Stop();
        var log = new FileLog(paths.LogDir, mirror: console ? Console.WriteLine : null);
        FailureLog.Journal = log;
        if (orphan)
        {
            log.Error("service: started by the service control manager, but the handshake with it failed; exiting");
            return 1;
        }
        if (scm is null && !console)
            Console.Error.WriteLine("zaprett-svc: not started by the service control manager; running as a console program (--console)");
        // main.autostart decides the engine at the first start in a Windows session only; a restart of the service
        // (update, repair, recovery) keeps what the user had (volatile HKLM\SOFTWARE\zaprett\Boot)
        bool osBoot = !(asSystem && !console) || new BootMarker(log).FirstStartSinceBoot();
        log.Info($"service start: {(osBoot ? "the first since Windows started" : "a restart within this Windows session")}");
        var clock = new SystemClock();
        var runner = new ProcessRunner();
        // dynamic port ranges: WMI templates (typed) + netsh per IP family (an IPv6-only setting); any overlap counts;
        // each source has CombinedDynamicPorts.DefaultSourceTimeout, the decision EngineIsolation.DefaultPortsTimeout
        var isolation = new EngineIsolation(runner, paths, log,
            dynamicPorts: new CombinedDynamicPorts(CombinedDynamicPorts.DefaultSourceTimeout, log, new WmiDynamicPorts(log), new NetshDynamicPorts(runner))
            {
                HistoryFile = Path.Combine(paths.RunDir, "dynamic-ports.json"),
                // the sources are slow after every boot: nothing to warn about then (Win10 cold start, 2026-09-24)
                OsBoot = osBoot,
            });
        // decided once per run: read the ranges now (WMI takes seconds after a boot) while the host and the core start
        _ = isolation.PermanentMainIsolationAsync(CancellationToken.None);
        var platform = new PlatformServices(
            paths, clock, runner,
            new EngineControl(log, clock, EngineRestartPolicy.Winws, isolation, paths.RunDir),
            new HttpProbe(),
            new DnsResolver(),
            new DnsControl(runner, paths, log),
            new FirewallControl(runner, log),
            new ConflictScanner(paths, log),
            new SystemInfo(paths, log),
            log);
        // where the start time goes after a boot or a fresh install: runtime load before Main, the SCM handshake, the
        // data directory check (ACLs)
        DateTime processStart;
        using (var me = System.Diagnostics.Process.GetCurrentProcess())
            processStart = me.StartTime;
        string scmText = scm?.StartedAt is { } at ? $"SCM connected {(at - processStart).TotalSeconds:0.0} s" : "not under the SCM";
        log.Info($"service starting (install {paths.InstallDir}, data {paths.DataDir}, {(console ? "console" : "service")} mode; " +
                 $"Main {(mainAt - processStart).TotalSeconds:0.0} s after the process start, {scmText}, " +
                 $"data directory {dataDirTime.Elapsed.TotalSeconds:0.0} s, now {(DateTime.Now - processStart).TotalSeconds:0.0} s)");
        if (scm?.StopRequested.IsCompleted == true)
        {
            log.Info("service stopped before it had started");
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        // the SCM is served by ScmConnection since the first line of Main, not by the host's WindowsServiceLifetime;
        // under it the host has no lifetime of its own, the console lifetime (Ctrl+C) is for the other cases
        if (scm is not null)
            builder.Services.AddSingleton<IHostLifetime, ScmHostLifetime>();
        else
            builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
        // shorter than what a stop from the SCM may take (StopSequence.StopWait): the host ends before the process must
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = StopSequence.HostShutdownTimeout);
        builder.Services.AddSingleton(platform);
        var coreOptions = new CoreOptions
        {
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            // S-5: TCP checks are isolated by the filters EngineControl puts on "main" and "test"
            IsolationSupported = true,
            TestLocalPortFrom = isolation.PortFrom,
            TestLocalPortTo = isolation.PortTo,
            NetworkList = new NetworkList(),
            Updates = new UpdateControlStub(),
        };
        builder.Services.AddSingleton(new ServiceMode(asSystem && !console));
        // "show the icon at sign-in" (HKLM Run value): the saved choice is made true again at every start, as SYSTEM
        var tray = new TrayAutostart(Path.Combine(paths.InstallDir, UiRelaunch.UiExeName), log);
        if (asSystem && !console)
            tray.ApplyRemembered();
        var core = DispatcherFactory.Create(platform, stub, log, coreOptions);
        // the service's own methods (tray.autostart, status.tray_autostart) in front of the core
        builder.Services.AddSingleton<ICommandDispatcher>(new ServiceMethods(core, tray, log));
        builder.Services.AddSingleton(core is CommandDispatcher coreDispatcher
            ? new CoreStartup(ct => coreDispatcher.StartupAsync(osBoot, ct))
            : new CoreStartup(_ => Task.FromResult(new System.Text.Json.Nodes.JsonObject { ["ok"] = true, ["stub"] = true })));
        builder.Services.AddSingleton(new StartupGate());
        builder.Services.AddSingleton(new PipeServerOptions { PipeName = pipeName });
        builder.Services.AddHostedService<IpcHostService>();
        builder.Services.AddHostedService<SchedulerService>();
        // D2: the tray the MSI closed for the update comes back in the sessions it ran in
        builder.Services.AddSingleton(new UiRelaunch(paths, new Zaprett.Service.Native.WtsUserSessions(log), log,
            installer: new WindowsInstallerMutex(log)));
        builder.Services.AddHostedService<UiRelaunchService>();

        using var host = builder.Build();
        return await HostRun.RunAsync(host, scm?.StopRequested, log).ConfigureAwait(false);
    }
}
