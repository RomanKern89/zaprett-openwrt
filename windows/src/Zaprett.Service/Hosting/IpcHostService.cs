using Microsoft.Extensions.Hosting;
using Zaprett.Core;
using Zaprett.Core.Platform;
using Zaprett.Service.Ipc;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Hosting;

/// <summary>Runs the pipe server and forwards dispatcher and engine events to subscribers; stops engines on shutdown.</summary>
public sealed class IpcHostService(ICommandDispatcher dispatcher, PlatformServices platform, PipeServerOptions options,
    IHostApplicationLifetime lifetime, ServiceMode mode, StartupGate gate, CoreStartup? startup = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var membership = new NetLocalGroupMembership();
        var server = new PipeServer(dispatcher, p => CallerResolver.FromPipe(p, membership, platform.Log), platform.Log, options);
        dispatcher.Event += server.Publish;
        var engine = platform.Engine as EngineControl;
        if (engine is not null)
            engine.Event += server.Publish;
        platform.Log.Info($@"ipc: listening on \\.\pipe\{options.PipeName}");
        try
        {
            var serving = server.RunAsync(stoppingToken);
            await StartCoreAsync(stoppingToken).ConfigureAwait(false);
            await serving.ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            // somebody else owns the pipe (another service instance): nothing to serve
            platform.Log.Error("ipc: " + e.Message);
            // a failure exit code lets the service control manager apply its recovery actions (restart later)
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        finally
        {
            dispatcher.Event -= server.Publish;
            if (engine is not null)
                engine.Event -= server.Publish;
        }
    }

    /// <summary>MSI options once (real service only), then the core's own start: rollback of a lost automatic
    /// selection and the engine when enabled. The pipe already answers meanwhile.</summary>
    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            if (mode.IsService)
                await InstallOptions.ApplyOnceAsync(dispatcher, platform.Paths, platform.Log, InstallOptions.Read(), ct).ConfigureAwait(false);
            if (startup is not null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await startup.Run(ct).ConfigureAwait(false);
                platform.Log.Info($"core startup ({sw.Elapsed.TotalSeconds:0.0} s): " + r.ToJsonString());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            // the service keeps serving the pipe: the user can see and fix the state
            platform.Log.Error("core startup failed: " + e);
        }
        finally
        {
            // the scheduler (watchdog, monitor, autoupdate) starts only now
            gate.Open();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (platform.Engine is EngineControl engine)
            await engine.DisposeAsync().ConfigureAwait(false);
        platform.Log.Info("service stopped");
    }
}
