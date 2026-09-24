namespace Zaprett.Service.Hosting;

/// <summary>
/// What a stop or shutdown from the SCM does, apart from the status plumbing of <see cref="ScmConnection"/> (testable
/// without an SCM): signal the host at once, then wait for it within a limit. A host that does not end within the limit
/// is not left running after the SCM has been told STOPPED (its pipe would refuse the next start): the stop returns,
/// STOPPED is reported (exit code 0: a stop the user or the MSI asked for is no failure for the recovery actions), and a
/// moment later the process exits; the engines go with it (Job Object, KILL_ON_JOB_CLOSE). Exiting inside the stop
/// instead left the service in STOP_PENDING, which the SCM takes for a crash and restarts.
/// </summary>
public sealed class StopSequence(Action<int>? exit = null, TimeSpan? stopWait = null, TimeSpan? shutdownWait = null,
    TimeSpan? exitDelay = null)
{
    /// <summary>Time for STOPPED to reach the SCM after the stop returned, before the process exits.</summary>
    public static readonly TimeSpan ExitDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>What a stop may take: the host's own ShutdownTimeout is shorter (<see cref="HostShutdownTimeout"/>).</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Windows gives services a few seconds at shutdown (WaitToKillServiceTimeout, 5 s by default).</summary>
    public static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(4);

    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _hostDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<int> _exit = exit ?? Environment.Exit;
    private readonly TimeSpan _stopWait = stopWait ?? StopWait;
    private readonly TimeSpan _shutdownWait = shutdownWait ?? ShutdownWait;
    private readonly TimeSpan _exitDelay = exitDelay ?? ExitDelay;

    public Task StopRequested => _stopRequested.Task;

    public void HostEnded() => _hostDone.TrySetResult();

    /// <summary>The stop is asked for (the SCM handler sets it before it hands the stop to a pool thread).</summary>
    public void Request() => _stopRequested.TrySetResult();

    public void Stop(Action askForTime)
    {
        _stopRequested.TrySetResult();
        try
        {
            askForTime();
        }
        catch (InvalidOperationException)
        {
            // not in STOP_PENDING (a stop while the service reports another state): the default wait applies
        }
        if (!_hostDone.Task.Wait(_stopWait))
        {
            Console.Error.WriteLine($"zaprett-svc: the stop takes longer than {_stopWait.TotalSeconds:0} s, exiting");
            var exiting = new Thread(() =>
            {
                Thread.Sleep(_exitDelay);
                _exit(1);
            })
            { IsBackground = true, Name = "stop-timeout-exit" };
            exiting.Start();
        }
    }

    public void Shutdown()
    {
        _stopRequested.TrySetResult();
        // Windows ends the process after its own limit anyway; the engines end with it (Job Object)
        _hostDone.Task.Wait(_shutdownWait);
    }
}
