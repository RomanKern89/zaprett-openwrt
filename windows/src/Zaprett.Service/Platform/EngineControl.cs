using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>Restart policy of ARCHITECTURE-WIN §7 (defaults) — shortened in tests.</summary>
public sealed record EngineRestartPolicy(
    TimeSpan RestartDelay,
    TimeSpan CrashWindow,
    int CrashLimit,
    TimeSpan Cooldown,
    TimeSpan StartupGrace,
    string? ReadyMarker = null,
    TimeSpan? ReadyTimeout = null,
    string? WaitingMarker = null)
{
    /// <summary>winws with --ssid-filter/--nlm-filter and no such network: it waits and opens WinDivert only when the
    /// network appears (nfqws.c). The instance is up, in phase "waiting_network".</summary>
    public const string WinwsWaitingMarker = "logical network is not present";

    /// <summary>winws prints this once the WinDivert filter is open (S-1: 0.1–0.85 s on the spike VM).</summary>
    public const string WinwsReadyMarker = "windivert initialized";

    public static EngineRestartPolicy Default { get; } =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), 5, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));

    /// <summary>The service's policy: an instance is up only after <see cref="WinwsReadyMarker"/> (S-1).</summary>
    public static EngineRestartPolicy Winws { get; } = Default with
    {
        ReadyMarker = WinwsReadyMarker, ReadyTimeout = TimeSpan.FromSeconds(5), WaitingMarker = WinwsWaitingMarker,
    };
}

/// <summary>
/// <see cref="IEngineControl"/>: each instance ("main", "test") is one child process in its own Job Object with
/// KILL_ON_JOB_CLOSE, stdout/stderr go to the journal. An instance that exits on its own is restarted after
/// <see cref="EngineRestartPolicy.RestartDelay"/>; after <see cref="EngineRestartPolicy.CrashLimit"/> exits within
/// <see cref="EngineRestartPolicy.CrashWindow"/> it pauses for <see cref="EngineRestartPolicy.Cooldown"/> and raises
/// a "status" event. With a <see cref="EngineRestartPolicy.ReadyMarker"/> an instance counts as started only after
/// that line appears in its output. With <see cref="EngineIsolation"/> (S-5) instance "test" runs with the filter that
/// sees only the local TCP ports of the check connections and "main" with the one that excludes them: permanently when
/// the dynamic port ranges of Windows allow it (then a selection never restarts main), otherwise only while "test" runs.
/// </summary>
public sealed class EngineControl : IEngineControl, IAsyncDisposable
{
    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly EngineRestartPolicy _policy;
    private readonly object _lock = new();
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    // start/stop of instances run one at a time
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly EngineIsolation? _isolation;
    // where the debug output of an instance started with --debug=1 goes (EngineDebugLog); null: into the journal
    private readonly string? _runDir;
    private readonly Dictionary<string, int> _starting = new(StringComparer.Ordinal);

    private static readonly TimeSpan StopWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputWaitLimit = TimeSpan.FromSeconds(2);

    // the phases of Zaprett.Core (EngineState.Phase)
    public const string PhaseStarting = EnginePhases.Starting;
    public const string PhaseWaitingNetwork = EnginePhases.WaitingNetwork;
    public const string PhaseCapturing = EnginePhases.Capturing;

    public const string MainInstance = "main";
    public const string TestInstance = "test";

    /// <summary>Engine events for subscribers: ("status", {engine:{instance, state, ...}}).</summary>
    public event Action<string, JsonObject>? Event;

    public EngineControl(ILog log, IClock clock, EngineRestartPolicy? policy = null, EngineIsolation? isolation = null,
        string? runDir = null)
    {
        _log = log;
        _clock = clock;
        _policy = policy ?? EngineRestartPolicy.Default;
        _isolation = isolation;
        _runDir = runDir;
    }

    public static bool IsValidInstanceName(string name) =>
        name.Length is > 0 and <= 32 && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public async Task<EngineState> StartAsync(string instance, string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!IsValidInstanceName(instance))
            throw new ArgumentException("invalid engine instance name", nameof(instance));
        // from here until StartAsync returns the instance counts as running (phase "starting"): the watchdog must not
        // see "not running" while a start asked by someone else is in progress (the Windows 11 test machine, "enable" + watchdog tick)
        using (MarkStarting(instance))
            return await StartGatedAsync(instance, executable, args, ct).ConfigureAwait(false);
    }

    private StartingMark MarkStarting(string instance)
    {
        lock (_lock)
            _starting[instance] = _starting.GetValueOrDefault(instance) + 1;
        return new StartingMark(this, instance);
    }

    private readonly struct StartingMark(EngineControl owner, string instance) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lock)
            {
                if (--owner._starting[instance] == 0)
                    owner._starting.Remove(instance);
            }
        }
    }

    private async Task<EngineState> StartGatedAsync(string instance, string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(instance, ct).ConfigureAwait(false);
            if (_isolation is null)
                return await StartCoreAsync(instance, executable, args, args, false, ct).ConfigureAwait(false);
            if (instance == TestInstance)
            {
                var candidate = await IsolatedArgsAsync(instance, true, executable, args, ct).ConfigureAwait(false);
                if (candidate is null)
                    return new EngineState(instance, false, null, null, 0);
                // with permanent isolation main already excludes the check ports; a main without the filter (started
                // before a service update, filter not built, or no permanent isolation) is restarted with it, once
                await IsolateMainAsync(ct).ConfigureAwait(false);
                return await StartCoreAsync(instance, executable, args, candidate, true, ct).ConfigureAwait(false);
            }
            if (instance == MainInstance && (Exists(TestInstance) || await _isolation.PermanentMainIsolationAsync(ct).ConfigureAwait(false)))
            {
                // permanent: main always excludes the local ports of the check connections, so a selection never
                // restarts it. No filter (e.g. an engine without --wf-save): main runs with its own arguments.
                var main = await IsolatedArgsAsync(instance, false, executable, args, ct).ConfigureAwait(false);
                return await StartCoreAsync(instance, executable, args, main ?? args, main is not null, ct).ConfigureAwait(false);
            }
            return await StartCoreAsync(instance, executable, args, args, false, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>?> IsolatedArgsAsync(string instance, bool candidate, string executable,
        IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            return await _isolation!.IsolateAsync(instance, candidate, executable, args, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Error($"engine {instance}: isolation filter failed: {e.Message}");
            return null;
        }
    }

    private bool Exists(string instance)
    {
        lock (_lock)
            return _instances.ContainsKey(instance);
    }

    /// <summary>Without permanent isolation: after the selection a running main gets its own arguments back.</summary>
    private async Task RestoreMainAsync(CancellationToken ct)
    {
        Instance? main;
        lock (_lock)
            _instances.TryGetValue(MainInstance, out main);
        if (main is null || !main.Isolated)
            return;
        string exe = main.Executable;
        var original = main.OriginalArgs;
        using (MarkStarting(MainInstance))
        {
            await StopCoreAsync(MainInstance, ct).ConfigureAwait(false);
            await StartCoreAsync(MainInstance, exe, original, original, false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Restarts a running "main" that has no exclusion filter yet with it. Nothing happens when main is
    /// already isolated, not running, or the filter still cannot be built (then the candidate is also seen by main).</summary>
    private async Task IsolateMainAsync(CancellationToken ct)
    {
        Instance? main;
        lock (_lock)
            _instances.TryGetValue(MainInstance, out main);
        if (main is null || main.Isolated)
            return;
        var isolated = await IsolatedArgsAsync(MainInstance, false, main.Executable, main.OriginalArgs, ct).ConfigureAwait(false);
        if (isolated is null)
            return;
        string exe = main.Executable;
        var original = main.OriginalArgs;
        using (MarkStarting(MainInstance))
        {
            await StopCoreAsync(MainInstance, ct).ConfigureAwait(false);
            await StartCoreAsync(MainInstance, exe, original, isolated, true, ct).ConfigureAwait(false);
        }
    }

    private async Task<EngineState> StartCoreAsync(string instance, string executable, IReadOnlyList<string> originalArgs,
        IReadOnlyList<string> args, bool isolated, CancellationToken ct)
    {
        var inst = new Instance(instance, executable, args.ToArray()) { OriginalArgs = originalArgs.ToArray(), Isolated = isolated };
        lock (_lock)
            _instances[instance] = inst;
        if (!Launch(inst))
        {
            lock (_lock)
                _instances.Remove(instance);
            inst.Job.Dispose();
            inst.Cts.Dispose();
            return new EngineState(instance, false, null, null, 0);
        }
        inst.Supervisor = Task.Run(() => SuperviseAsync(inst), CancellationToken.None);
        var proc = inst.Process!;
        if (_policy.ReadyMarker is null)
        {
            // up = still alive after the grace period
            await Task.WhenAny(proc.Exited, _clock.Delay(_policy.StartupGrace, ct)).ConfigureAwait(false);
            return ActualState(instance);
        }
        // up = the ready line appeared (S-1: --dry-run does not open the WinDivert filter, only a real start does)
        var readyTimeout = _policy.ReadyTimeout ?? TimeSpan.FromSeconds(5);
        var ready = inst.Ready!.Task;
        var waiting = inst.Waiting!.Task;
        await Task.WhenAny(ready, waiting, proc.Exited, _clock.Delay(readyTimeout, ct)).ConfigureAwait(false);
        if (ready.IsCompleted)
            return ActualState(instance);
        if (waiting.IsCompleted && !proc.Exited.IsCompleted)
        {
            // network_filter: up and waiting for the network; WinDivert opens when it appears ("capturing" then)
            _log.Info($"engine {instance}: waiting for the selected network (network_filter), WinDivert opens when it appears");
            return ActualState(instance);
        }
        if (!proc.Exited.IsCompleted)
        {
            _log.Error($"engine {instance}: no '{_policy.ReadyMarker}' within {readyTimeout.TotalSeconds:0.#} s, stopping it");
            await StopCoreAsync(instance, ct).ConfigureAwait(false);
            return new EngineState(instance, false, null, null, 0);
        }
        return ActualState(instance);
    }

    public async Task StopAsync(string instance, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(instance, ct).ConfigureAwait(false);
            // with permanent isolation main is left alone; otherwise it gets its own arguments back
            if (_isolation is not null && instance == TestInstance && !await _isolation.PermanentMainIsolationAsync(ct).ConfigureAwait(false))
                await RestoreMainAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(string instance, CancellationToken ct)
    {
        Instance? inst;
        lock (_lock)
        {
            if (!_instances.Remove(instance, out inst))
                return;
        }
        await inst.Cts.CancelAsync().ConfigureAwait(false);
        lock (inst)
            inst.Process?.Kill();
        // the supervisor ends right after the cancellation; waiting without ct keeps the cleanup below certain
        if (inst.Supervisor is not null)
            await inst.Supervisor.ConfigureAwait(false);
        // the old process must be gone (its WinDivert handle closed) before a new instance can start
        Task<int>? exited;
        lock (inst)
            exited = inst.Process?.Exited;
        if (exited is not null && await Task.WhenAny(exited, Task.Delay(StopWaitLimit, CancellationToken.None)).ConfigureAwait(false) != exited)
            _log.Warn($"engine {instance}: the process did not exit within {StopWaitLimit.TotalSeconds:0} s after it was killed");
        // the output pump ends right after the exit: the last lines (and the debug file's buffer) are written out
        Task? output;
        lock (inst)
            output = inst.Output;
        if (output is not null)
            await Task.WhenAny(output, Task.Delay(OutputWaitLimit, CancellationToken.None)).ConfigureAwait(false);
        lock (inst)
            inst.Process?.Dispose();
        inst.Job.Dispose();
        inst.Cts.Dispose();
        _log.Info($"engine {instance}: stopped");
    }

    public EngineState GetState(string instance) => StateOf(instance, honourStarting: true);

    private EngineState ActualState(string instance) => StateOf(instance, honourStarting: false);

    private EngineState StateOf(string instance, bool honourStarting)
    {
        Instance? inst;
        bool starting;
        lock (_lock)
        {
            _instances.TryGetValue(instance, out inst);
            starting = honourStarting && _starting.ContainsKey(instance);
        }
        if (inst is null)
            return starting
                ? new EngineState(instance, true, null, null, 0, PhaseStarting)
                : new EngineState(instance, false, null, null, 0);
        lock (inst)
        {
            bool running = inst.Process is { } p && !p.Exited.IsCompleted;
            if (!running && starting)
                return new EngineState(instance, true, null, null, CrashesInWindow(inst), PhaseStarting);
            // a start in progress replaces this process: it is "starting" until StartAsync returns
            string phase = starting ? PhaseStarting : inst.Phase;
            return new EngineState(instance, running, running ? inst.Process!.Pid : null, running ? inst.StartedAt : null,
                CrashesInWindow(inst), running ? phase : PhaseCapturing);
        }
    }

    /// <summary>Phase of a running instance: starting, waiting_network (network_filter, no such network yet) or
    /// capturing; null when it does not run.</summary>
    public string? GetPhase(string instance)
    {
        Instance? inst;
        lock (_lock)
            _instances.TryGetValue(instance, out inst);
        if (inst is null)
            return null;
        lock (inst)
            return inst.Process is { } p && !p.Exited.IsCompleted ? inst.Phase : null;
    }

    /// <summary>Process ids in an instance's job (the engine and anything it started).</summary>
    public IReadOnlyList<int> GetJobProcessIds(string instance)
    {
        lock (_lock)
            return _instances.TryGetValue(instance, out var inst) ? inst.Job.GetProcessIds() : [];
    }

    private int CrashesInWindow(Instance inst)
    {
        var cutoff = _clock.Now - _policy.CrashWindow;
        inst.Crashes.RemoveAll(t => t < cutoff);
        return inst.Crashes.Count;
    }

    private bool Launch(Instance inst)
    {
        try
        {
            var proc = Win32Process.Start(inst.Executable, inst.Args, Path.GetDirectoryName(inst.Executable), inst.Job);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (inst)
            {
                inst.Process?.Dispose();
                inst.Process = proc;
                inst.StartedAt = _clock.Now;
                inst.Ready = ready;
                inst.Waiting = waiting;
                inst.Phase = _policy.ReadyMarker is null ? PhaseCapturing : PhaseStarting;
            }
            _log.Info($"engine {inst.Name}: started pid {proc.Pid}: {WindowsCommandLine.Build(inst.Executable, inst.Args)}");
            var debug = _runDir is not null && EngineDebugLog.IsConsoleDebug(inst.Args)
                ? new EngineDebugLog(EngineDebugLog.FileFor(_runDir, inst.Name), _log)
                : null;
            if (debug is not null)
                _log.Info($"engine {inst.Name}: debug output goes to {debug.Path}");
            var output = PumpAsync(inst, "out", proc.StdOut, ready, waiting, debug);
            lock (inst)
                inst.Output = output;
            _ = PumpAsync(inst, "err", proc.StdErr, ready, waiting, null);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or IOException or ArgumentException)
        {
            _log.Error($"engine {inst.Name}: cannot start {inst.Executable}: {e.Message}");
            return false;
        }
    }

    private async Task PumpAsync(Instance inst, string stream, Stream s, TaskCompletionSource ready, TaskCompletionSource waiting,
        EngineDebugLog? debug)
    {
        string instance = inst.Name;
        try
        {
            using var reader = new StreamReader(s, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                if (line.Length > 0)
                {
                    debug?.WriteLine(line);
                    bool isMarker = true;
                    if (_policy.ReadyMarker is { } marker && line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (inst)
                            if (ReferenceEquals(inst.Ready, ready))
                                inst.Phase = PhaseCapturing;
                        ready.TrySetResult();
                    }
                    else if (_policy.WaitingMarker is { } wait && line.Contains(wait, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (inst)
                            if (ReferenceEquals(inst.Ready, ready))
                                inst.Phase = PhaseWaitingNetwork;
                        waiting.TrySetResult();
                    }
                    else
                    {
                        isMarker = false;
                    }
                    // with debugging the output is thousands of lines: the journal keeps only the ready/waiting lines
                    if (debug is null || isMarker)
                        _log.Info($"engine {instance} [{stream}]: {(line.Length > 2000 ? line[..2000] : line)}");
                }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            // the engine's output ended (it exited or was stopped): the rest of the buffer goes to the file
            debug?.Dispose();
        }
    }

    private async Task SuperviseAsync(Instance inst)
    {
        var ct = inst.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            int code;
            try
            {
                code = await inst.Process!.Exited.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            int crashes;
            lock (inst)
            {
                inst.Crashes.Add(_clock.Now);
                crashes = CrashesInWindow(inst);
            }
            _log.Warn($"engine {inst.Name}: exited with code {code} ({crashes} in {_policy.CrashWindow.TotalMinutes:0} min)");
            var delay = _policy.RestartDelay;
            if (crashes >= _policy.CrashLimit)
            {
                delay = _policy.Cooldown;
                _log.Error($"engine {inst.Name}: {crashes} crashes, pausing restarts for {_policy.Cooldown.TotalMinutes:0.#} min");
                Raise(inst.Name, "paused", code, crashes);
            }
            else
            {
                Raise(inst.Name, "crashed", code, crashes);
            }
            try
            {
                await _clock.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (crashes >= _policy.CrashLimit)
                lock (inst)
                    inst.Crashes.Clear();
            if (ct.IsCancellationRequested)
                break;
            while (!Launch(inst))
            {
                // the executable itself is gone or broken: keep trying at the cooldown pace
                int count;
                lock (inst)
                {
                    inst.Crashes.Add(_clock.Now);
                    count = CrashesInWindow(inst);
                }
                Raise(inst.Name, "paused", null, count);
                try
                {
                    await _clock.Delay(_policy.Cooldown, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            Raise(inst.Name, "restarted", null, crashes);
        }
    }

    private void Raise(string instance, string state, int? exitCode, int crashes)
    {
        var engine = new JsonObject { ["instance"] = instance, ["state"] = state, ["restarts"] = crashes };
        if (exitCode is not null)
            engine["exit_code"] = exitCode;
        try
        {
            Event?.Invoke("status", new JsonObject { ["engine"] = engine });
        }
        catch (Exception e)
        {
            _log.Warn("engine event handler failed: " + e.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        string[] names;
        lock (_lock)
            names = [.. _instances.Keys];
        foreach (var n in names)
            await StopAsync(n, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class Instance(string name, string executable, string[] args)
    {
        public string Name { get; } = name;
        public string Executable { get; } = executable;
        public string[] Args { get; } = args;
        public JobObject Job { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public Win32Process? Process { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public List<DateTimeOffset> Crashes { get; } = [];
        public string[] OriginalArgs { get; init; } = [];
        public bool Isolated { get; init; }
        public TaskCompletionSource? Ready { get; set; }
        public TaskCompletionSource? Waiting { get; set; }
        public string Phase { get; set; } = PhaseStarting;
        public Task? Supervisor { get; set; }
        public Task? Output { get; set; }
    }
}
