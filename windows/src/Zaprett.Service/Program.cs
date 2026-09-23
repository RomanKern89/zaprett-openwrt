using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zaprett.Core;
using Zaprett.Core.Platform;
using Zaprett.Ipc;
using Zaprett.Service.Hosting;
using Zaprett.Service.Ipc;
using Zaprett.Service.Platform;

// zaprett-svc: Windows service "zaprett" (ARCHITECTURE-WIN §2 W-2).
//   zaprett-svc                 run under the service control manager
//   zaprett-svc --console       run in this console (development); Ctrl+C stops
//   options: --stub (no core, stub dispatcher), --pipe <name> (or ZAPRETT_PIPE; default "zaprett")
//   ZAPRETT_INSTALL_DIR / ZAPRETT_DATA_DIR override the Program Files / ProgramData locations.
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
var log = new FileLog(paths.LogDir, mirror: console ? Console.WriteLine : null);
var clock = new SystemClock();
var runner = new ProcessRunner();
// dynamic port ranges: WMI templates (typed) + netsh per IP family (an IPv6-only setting); any overlap counts
var isolation = new EngineIsolation(runner, paths, log,
    dynamicPorts: new CombinedDynamicPorts(new WmiDynamicPorts(log), new NetshDynamicPorts(runner)));
var platform = new PlatformServices(
    paths, clock, runner,
    new EngineControl(log, clock, EngineRestartPolicy.Winws, isolation),
    new HttpProbe(),
    new DnsResolver(),
    new DnsControl(runner, paths, log),
    new FirewallControl(runner, log),
    new ConflictScanner(paths, log),
    new SystemInfo(paths, log),
    log);
log.Info($"service starting (install {paths.InstallDir}, data {paths.DataDir}, {(console ? "console" : "service")} mode)");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "zaprett");
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
builder.Services.AddSingleton<ICommandDispatcher>(_ => DispatcherFactory.Create(platform, stub, log, coreOptions));
builder.Services.AddSingleton(new PipeServerOptions { PipeName = pipeName });
builder.Services.AddHostedService<IpcHostService>();
builder.Services.AddHostedService<SchedulerService>();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return Environment.ExitCode;
