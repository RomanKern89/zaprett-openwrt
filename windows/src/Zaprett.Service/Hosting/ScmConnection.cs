using System.Runtime.InteropServices;

namespace Zaprett.Service.Hosting;

/// <summary>
/// The service control manager handshake, made at the first line of Main (D13). The SCM waits 30 s for a started
/// service process to connect (StartServiceCtrlDispatcher). After a fresh install Defender scans every file as it is
/// first loaded: with System.ServiceProcess (ServiceBase) the handshake came 11.4 s after Main, 27.0 s after the process
/// start (the Windows 10 test machine, 2026-09-24). So it is made here with the Win32 calls directly, nothing but CoreLib: the dispatcher runs
/// on a thread of its own, ServiceMain reports RUNNING at once, and the data directory, the journal, the core and the
/// pipe are set up afterwards. A stop or shutdown from the SCM goes through <see cref="StopSequence"/>; a host that
/// ends by itself reports STOPPED with its exit code (the SCM's recovery actions apply).
/// </summary>
public sealed unsafe partial class ScmConnection
{
    public const string Name = "zaprett";

    // winsvc.h
    public const uint ControlStop = 1;
    public const uint ControlInterrogate = 4;
    public const uint ControlShutdown = 5;
    public const uint StateStopped = 1;
    public const uint StateStopPending = 3;
    public const uint StateRunning = 4;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint AcceptStop = 0x1;
    private const uint AcceptShutdown = 0x4;
    private const uint NoError = 0;
    private const uint ErrorCallNotImplemented = 120;
    private const uint ErrorServiceSpecific = 1066;
    // the SCM handshake of a process it started comes within its own 30 s limit
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(25);

    private static ScmConnection? s_current;
    private static readonly nint s_name = Marshal.StringToHGlobalUni(Name);

    private readonly TaskCompletionSource _decided = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<uint, uint, uint, uint> _report;
    private readonly object _lock = new();
    private Thread? _dispatcher;
    private nint _handle;
    private uint _checkPoint;
    private bool _stopped;

    private ScmConnection(StopSequence stop, Action<uint, uint, uint, uint>? report)
    {
        Sequence = stop;
        _report = report ?? SetStatus;
    }

    /// <summary>For tests: no dispatcher; <paramref name="report"/> gets (state, win32 exit code, service exit code,
    /// wait hint ms) instead of SetServiceStatus.</summary>
    internal static ScmConnection CreateDetached(StopSequence stop, Action<uint, uint, uint, uint> report) => new(stop, report);

    internal StopSequence Sequence { get; }

    /// <summary>The work a stop or shutdown control started (tests wait for it).</summary>
    internal Task? Pending { get; private set; }

    /// <summary>When the SCM started the service (ServiceMain); null until then.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Completes when the SCM asks the service to stop (or the system shuts down).</summary>
    public Task StopRequested => Sequence.StopRequested;

    /// <summary>Connects to the SCM on a thread of its own; null when this process was not started by the SCM (the
    /// dispatcher refuses at once, ERROR_FAILED_SERVICE_CONTROLLER_CONNECT).</summary>
    public static ScmConnection? Connect()
    {
        var svc = new ScmConnection(new StopSequence(), null);
        s_current = svc;
        svc._dispatcher = new Thread(() =>
        {
            try
            {
                var table = stackalloc ServiceTableEntry[2];
                table[0] = new ServiceTableEntry { Name = (char*)s_name, Proc = &ServiceMain };
                table[1] = default;
                StartServiceCtrlDispatcherW(table);
            }
            finally
            {
                svc._decided.TrySetResult();
            }
        })
        { IsBackground = true, Name = "scm-dispatcher" };
        svc._dispatcher.Start();
        svc._decided.Task.Wait(ConnectWait);
        return svc.StartedAt is null ? null : svc;
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(uint argc, char** argv)
    {
        var svc = s_current;
        if (svc is null)
            return;
        svc._handle = RegisterServiceCtrlHandlerExW((char*)s_name, &Handler, 0);
        if (svc._handle == 0)
        {
            // no handler, no status: Connect returns null and Main ends as a service without a handshake (orphan)
            svc._decided.TrySetResult();
            return;
        }
        svc.StartedAt = DateTime.Now;
        // RUNNING at once: everything else is done after the handshake, while the SCM already counts the service up
        svc._report(StateRunning, NoError, 0, 0);
        svc._decided.TrySetResult();
    }

    [UnmanagedCallersOnly]
    private static uint Handler(uint control, uint eventType, nint eventData, nint context) =>
        s_current?.OnControl(control) ?? ErrorCallNotImplemented;

    /// <summary>A control from the SCM. It returns at once (the SCM waits for handlers); the stop runs on a pool thread
    /// and reports STOPPED when the host has ended or the wait gave up.</summary>
    internal uint OnControl(uint control)
    {
        switch (control)
        {
            case ControlInterrogate:
                return NoError;
            case ControlStop:
            case ControlShutdown:
                lock (_lock)
                {
                    if (Pending is not null || _stopped)
                        return NoError;
                    // at once, not on the pool thread: a host ending by itself right now must see that the stop was asked
                    // for (else it would report its own STOPPED first)
                    Sequence.Request();
                    bool shutdown = control == ControlShutdown;
                    var wait = shutdown ? StopSequence.ShutdownWait : StopSequence.StopWait;
                    Pending = Task.Run(() =>
                    {
                        if (shutdown)
                        {
                            ReportPending(wait);
                            Sequence.Shutdown();
                        }
                        else
                        {
                            Sequence.Stop(() => ReportPending(wait));
                        }
                        Stopped(NoError, 0);
                    });
                }
                return NoError;
            default:
                return ErrorCallNotImplemented;
        }
    }

    /// <summary>The host has ended: a stop the SCM did not ask for is reported with the exit code (a failure makes the
    /// recovery actions apply); then the dispatcher thread ends and the process may exit.</summary>
    public void HostFinished(int exitCode)
    {
        Sequence.HostEnded();
        // decided and reported under the lock of the STOP handler: a STOP arriving now either came first (its task
        // reports STOPPED 0) or finds the service stopped (it does nothing); never both, never STOP_PENDING after STOPPED
        lock (_lock)
        {
            if (Pending is null && !_stopped)
            {
                _stopped = true;
                AfterStopDecision?.Invoke();
                Report(StateStopped, exitCode == 0 ? NoError : ErrorServiceSpecific, (uint)exitCode, TimeSpan.Zero);
            }
        }
        Pending?.Wait(StopSequence.StopWait);
        _dispatcher?.Join(StopSequence.StopWait);
    }

    /// <summary>For tests: runs inside HostFinished between its decision and its report (to force a STOP there).</summary>
    internal Action? AfterStopDecision { get; set; }

    private void Stopped(uint win32Exit, uint serviceExit)
    {
        lock (_lock)
        {
            if (_stopped)
                return;
            _stopped = true;
            Report(StateStopped, win32Exit, serviceExit, TimeSpan.Zero);
        }
    }

    // nothing is reported after STOPPED (SetServiceStatus must not follow it)
    private void ReportPending(TimeSpan wait)
    {
        lock (_lock)
        {
            if (!_stopped)
                Report(StateStopPending, NoError, 0, wait);
        }
    }

    private void Report(uint state, uint win32Exit, uint serviceExit, TimeSpan waitHint) =>
        _report(state, win32Exit, serviceExit, (uint)waitHint.TotalMilliseconds);

    private void SetStatus(uint state, uint win32Exit, uint serviceExit, uint waitHintMs)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == StateRunning ? AcceptStop | AcceptShutdown : 0,
            Win32ExitCode = win32Exit,
            ServiceSpecificExitCode = serviceExit,
            CheckPoint = state == StateStopPending ? Interlocked.Increment(ref _checkPoint) : 0,
            WaitHint = waitHintMs,
        };
        SetServiceStatus(_handle, &status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public char* Name;
        public delegate* unmanaged<uint, char**, void> Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW(ServiceTableEntry* table);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial nint RegisterServiceCtrlHandlerExW(char* name, delegate* unmanaged<uint, uint, nint, nint, uint> handler, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint handle, ServiceStatus* status);
}
