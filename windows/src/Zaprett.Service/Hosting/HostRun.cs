using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Hosting;

/// <summary>Runs the host until it ends by itself or the SCM asks for the stop.</summary>
public static class HostRun
{
    /// <summary>0 when the stop was asked for (also while the host was still starting: an MSI rollback or services.msc
    /// right after the start, when Host.StartAsync ends with OperationCanceledException), and whatever the stop itself
    /// ran into then (logged); else the host's exit code. The host is stopped once.</summary>
    public static async Task<int> RunAsync(IHost host, Task? stopRequested, ILog? log = null)
    {
        if (stopRequested is not null)
        {
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            _ = stopRequested.ContinueWith(_ => lifetime.StopApplication(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        bool asked() => stopRequested?.IsCompleted == true;
        try
        {
            await host.StartAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (asked())
        {
            // the services that did start are stopped as in a normal stop
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log?.Warn($"service: stopping after an interrupted start: {e.GetType().Name}: {e.Message}");
            }
            return 0;
        }
        try
        {
            // stops the host itself when the application is stopping (one StopAsync)
            await host.WaitForShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (asked())
        {
            // a stop past the host's ShutdownTimeout (OperationCanceledException) or a service failing to stop: the
            // stop was asked for all the same, and a failure code would make the SCM restart the service
            log?.Warn($"service: the stop ended with {e.GetType().Name}: {e.Message}");
        }
        return asked() ? 0 : Environment.ExitCode;
    }
}

/// <summary>Under the SCM the start and the stop come from <see cref="ScmConnection"/>: no console handlers (Ctrl+C,
/// ProcessExit) as a second, unsynchronized way to stop.</summary>
public sealed class ScmHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Where a failure that ends Main is written: the journal once it is open, and stderr.</summary>
internal static class FailureLog
{
    public static ILog? Journal { get; set; }

    public static void Report(Exception e)
    {
        Journal?.Error("service failed: " + e);
        Console.Error.WriteLine("zaprett-svc: " + e);
    }
}
