using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Zaprett.Core.Platform;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Hosting;

/// <summary>The signed-in sessions of this machine, and a launch in one of them as its user.</summary>
public interface IUserSessions
{
    /// <summary>Session ids in the WTSActive state (a user is signed in and attached).</summary>
    IReadOnlySet<int> ActiveSessions();

    /// <summary>Sessions where a process of exactly this exe runs.</summary>
    IReadOnlySet<int> SessionsRunning(string exePath);

    /// <summary>Starts exe in the session with the user's own (for a UAC admin: filtered, not elevated) token; the pid.</summary>
    int Launch(int sessionId, string exePath, string arguments, string workingDir);
}

/// <summary>Is a Windows Installer transaction running (install, update, removal)?</summary>
public interface IInstallerActivity
{
    bool IsRunning();
}

/// <summary>How long the relaunch waits: installer polling, the limit of that wait, the check that the tray lives.</summary>
public sealed record UiRelaunchTiming(TimeSpan Poll, TimeSpan MaxInstallerWait, TimeSpan CheckAfter)
{
    public static readonly UiRelaunchTiming Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
}

/// <summary>
/// D2: the MSI's prepare step closes the tray (zaprett-ui.exe) of every signed-in user so that no file is in use, and
/// records their sessions in run\ui-relaunch.json ({"version":1,"sessions":[1,3]}). The service brings the tray back in
/// each of those sessions that is still active. Only the session ids are taken from the file: the exe is always
/// zaprett-ui.exe of this installation.
/// D16: during an update the new service starts before the old product is removed, and the removal of 0.1.0 kills
/// every zaprett-ui.exe (ZaprettStopUi, taskkill /IM) 0.9 s after the relaunch (the Windows 11 test machine, 2026-09-24). So the tray is
/// started only once no installer transaction runs, it is checked a few seconds later and started once more if it was
/// killed, and the file is deleted only then (or when the wait gives up): a service restarted by the installer in
/// between finds the file and does the same.
/// </summary>
public sealed class UiRelaunch(WindowsPaths paths, IUserSessions sessions, ILog log, Func<DateTimeOffset>? now = null,
    IInstallerActivity? installer = null, UiRelaunchTiming? timing = null)
{
    public const string FileName = "ui-relaunch.json";
    public const string UiExeName = "zaprett-ui.exe";
    public const string TrayArgument = "--tray";
    public const int MaxBytes = 4096;
    public const int MaxSessions = 64;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    // a clock set back a little between the write and the service start is not a stale file
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly UiRelaunchTiming _timing = timing ?? UiRelaunchTiming.Default;

    public string FilePath => Path.Combine(paths.RunDir, FileName);

    public string UiExe => Path.Combine(paths.InstallDir, UiExeName);

    /// <summary>For callers without a loop of their own (tests).</summary>
    public IReadOnlyList<int> Run() => RunAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Starts the tray where the file asks for it; the sessions it runs in afterwards. A cancellation (the
    /// service stops) leaves the file for the next start.</summary>
    public async Task<IReadOnlyList<int>> RunAsync(CancellationToken ct)
    {
        var wanted = Read();
        if (wanted is null)
            return [];
        try
        {
            if (wanted.Count == 0)
                return [];
            if (!File.Exists(UiExe))
            {
                log.Warn($"tray relaunch: {UiExe} is missing, not started");
                return [];
            }
            if (!await InstallerDoneAsync(ct).ConfigureAwait(false))
                return [];
            var started = Launch(wanted, alreadyRunning: sessions.SessionsRunning(UiExe));
            if (started.Count > 0 && _timing.CheckAfter > TimeSpan.Zero)
                started = await CheckAsync(started, ct).ConfigureAwait(false);
            return started;
        }
        catch (OperationCanceledException)
        {
            wanted = null;
            throw;
        }
        finally
        {
            if (wanted is not null)
                Delete();
        }
    }

    /// <summary>Waits while an installer transaction runs; false when it still runs after the limit.</summary>
    private async Task<bool> InstallerDoneAsync(CancellationToken ct)
    {
        if (installer is null || !installer.IsRunning())
            return true;
        log.Info("tray relaunch: Windows Installer is still working, the tray starts when it has finished");
        var waited = TimeSpan.Zero;
        while (installer.IsRunning())
        {
            if (waited >= _timing.MaxInstallerWait)
            {
                log.Warn($"tray relaunch: Windows Installer still works after {_timing.MaxInstallerWait.TotalMinutes:0.#} min, the tray is not started");
                return false;
            }
            await Task.Delay(_timing.Poll, ct).ConfigureAwait(false);
            waited += _timing.Poll;
        }
        return true;
    }

    private List<int> Launch(IEnumerable<int> wanted, IReadOnlySet<int> alreadyRunning)
    {
        var active = sessions.ActiveSessions();
        var started = new List<int>();
        foreach (int id in wanted)
        {
            if (!active.Contains(id))
            {
                log.Info($"tray relaunch: session {id} is not active, skipped");
                continue;
            }
            if (alreadyRunning.Contains(id))
            {
                log.Info($"tray relaunch: the tray already runs in session {id}");
                continue;
            }
            try
            {
                int pid = sessions.Launch(id, UiExe, TrayArgument, paths.InstallDir);
                log.Info($"tray relaunch: started in session {id} (pid {pid})");
                started.Add(id);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                log.Warn($"tray relaunch: session {id}: {e.Message}");
            }
        }
        return started;
    }

    /// <summary>A few seconds after the start the tray must still run; where it does not, it is started once more.</summary>
    private async Task<List<int>> CheckAsync(List<int> started, CancellationToken ct)
    {
        await Task.Delay(_timing.CheckAfter, ct).ConfigureAwait(false);
        var running = sessions.SessionsRunning(UiExe);
        var gone = started.Where(id => !running.Contains(id)).ToList();
        if (gone.Count == 0)
            return started;
        log.Info($"tray relaunch: the tray ended in session(s) {string.Join(", ", gone)} right after the start, starting it once more");
        var again = Launch(gone, alreadyRunning: running);
        return started.Where(running.Contains).Concat(again).ToList();
    }

    /// <summary>The valid, fresh session ids of the file; null when there is no file. An unusable file is deleted.</summary>
    private List<int>? Read()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists)
            return null;
        byte[]? bytes = null;
        try
        {
            // a link is not followed: only a plain small file written by the installer is read
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                log.Warn("tray relaunch: the file is a link, ignored");
            else if (info.Length > MaxBytes)
                log.Warn($"tray relaunch: the file is larger than {MaxBytes} bytes, ignored");
            else
                bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"tray relaunch: cannot read the file: {e.Message}");
        }
        if (bytes is null)
            return [];
        var age = (now?.Invoke() ?? DateTimeOffset.Now) - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (age > MaxAge || age < -ClockSkew)
        {
            log.Info($"tray relaunch: the file is {age.TotalMinutes:0} min old, ignored");
            return [];
        }
        var ids = Parse(bytes);
        if (ids is null)
            log.Warn("tray relaunch: the file is not {\"version\":1,\"sessions\":[…]}, ignored");
        return ids ?? [];
    }

    private void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"tray relaunch: cannot delete the file: {e.Message}");
        }
    }

    /// <summary>Session ids of a version 1 file: distinct, positive (session 0 has no desktop), at most 64.</summary>
    public static List<int>? Parse(byte[] bytes)
    {
        try
        {
            // Windows PowerShell 5.1 writes UTF-8 with a BOM, which the byte parser does not skip
            ReadOnlySpan<byte> json = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes;
            if (JsonNode.Parse(json) is not JsonObject o || o["version"]?.GetValueKind() != JsonValueKind.Number ||
                o["version"]!.GetValue<double>() != 1 || o["sessions"] is not JsonArray list)
                return null;
            var ids = new List<int>();
            foreach (var n in list)
            {
                if (n?.GetValueKind() != JsonValueKind.Number || !n.AsValue().TryGetValue(out int id))
                    return null;
                if (id > 0 && !ids.Contains(id) && ids.Count < MaxSessions)
                    ids.Add(id);
            }
            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Windows Installer holds the mutex Global\_MSIExecute while a transaction executes (install, update,
/// removal, and the removal of the old product inside an update).</summary>
public sealed class WindowsInstallerMutex(ILog log) : IInstallerActivity
{
    private const string Name = @"Global\_MSIExecute";
    private bool _reported;

    public bool IsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(Name, out var m))
                return false;
            using (m)
            {
                try
                {
                    // free: taken and given back at once (no transaction waits on it for longer than this call)
                    if (!m.WaitOne(0))
                        return true;
                }
                catch (AbandonedMutexException)
                {
                    // the owner ended without releasing it: no transaction runs; this thread owns it now
                }
                m.ReleaseMutex();
                return false;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            if (!_reported)
            {
                _reported = true;
                log.Info($"tray relaunch: cannot check Windows Installer ({e.Message}), not waiting for it");
            }
            return false;
        }
    }
}

/// <summary>Runs <see cref="UiRelaunch"/> once the core has started (the tray then finds the service ready). Only as the
/// service: launching in another user's session needs LocalSystem.</summary>
public sealed class UiRelaunchService(UiRelaunch relaunch, StartupGate gate, ServiceMode mode, PlatformServices platform) : BackgroundService
{
    private static readonly TimeSpan StartupWaitLimit = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!mode.IsService)
            return;
        try
        {
            await gate.Done.WaitAsync(StartupWaitLimit, stoppingToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // the tray shows "service starting" by itself; the user must not stay without it
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await relaunch.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            platform.Log.Error($"tray relaunch: {e.Message}");
        }
    }
}
