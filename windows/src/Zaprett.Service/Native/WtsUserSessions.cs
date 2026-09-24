using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Zaprett.Core.Platform;
using Zaprett.Service.Hosting;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Native;

/// <summary>
/// Sessions through WTS and a launch with CreateProcessAsUser. The token is the one WTSQueryUserToken returns: the
/// user's normal token, which for a UAC admin is the filtered (not elevated) one; its linked elevated token is never
/// taken. Needs LocalSystem (SE_TCB_NAME), so only the service calls it.
/// </summary>
internal sealed unsafe partial class WtsUserSessions(ILog log) : IUserSessions
{
    private const int WTSActive = 0;
    private const int TokenElevationType = 18;
    private const int TokenElevationTypeFull = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFOW
    {
        public int SessionId;
        public nint pWinStationName;
        public int State;
    }

    public IReadOnlySet<int> ActiveSessions()
    {
        var set = new HashSet<int>();
        if (!WTSEnumerateSessionsW(0, 0, 1, out nint info, out int count))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var items = new ReadOnlySpan<WTS_SESSION_INFOW>((void*)info, count);
            foreach (var s in items)
            {
                if (s.State == WTSActive)
                    set.Add(s.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(info);
        }
        return set;
    }

    public IReadOnlySet<int> SessionsRunning(string exePath)
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (p)
            {
                try
                {
                    if (string.Equals(ProcessSnapshot.PathOf(p.Id), exePath, StringComparison.OrdinalIgnoreCase))
                        set.Add(p.SessionId);
                }
                catch (InvalidOperationException)
                {
                    // exited meanwhile
                }
            }
        }
        return set;
    }

    public int Launch(int sessionId, string exePath, string arguments, string workingDir)
    {
        if (!WTSQueryUserToken(sessionId, out nint token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"WTSQueryUserToken: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        nint env = 0;
        try
        {
            int type = 0;
            if (GetTokenInformation(token, TokenElevationType, &type, sizeof(int), out _) && type == TokenElevationTypeFull)
                // UAC off or the built-in Administrator: this elevated token is what the user's own desktop runs with
                log.Info($"tray relaunch: session {sessionId} runs without UAC filtering, the tray gets its normal token");
            if (!CreateEnvironmentBlock(out env, token, false))
                env = 0;
            string cmd = "\"" + exePath + "\" " + arguments;
            char[] buf = new char[cmd.Length + 1];
            cmd.CopyTo(0, buf, 0, cmd.Length);
            fixed (char* desktop = "winsta0\\default")
            fixed (char* pcmd = buf)
            {
                var si = new Win32.STARTUPINFOW { cb = sizeof(Win32.STARTUPINFOW), lpDesktop = (nint)desktop };
                uint flags = env != 0 ? Win32.CREATE_UNICODE_ENVIRONMENT : 0;
                if (!CreateProcessAsUserW(token, exePath, pcmd, 0, 0, false, flags, env, workingDir, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                Win32.CloseHandle(pi.hThread);
                Win32.CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
        }
        finally
        {
            if (env != 0)
                DestroyEnvironmentBlock(env);
            Win32.CloseHandle(token);
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessionsW(nint server, int reserved, int version, out nint sessionInfo, out int count);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(int sessionId, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, void* info, int length, out int returnLength);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUserW(nint token, string applicationName, char* commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        string currentDirectory, ref Win32.STARTUPINFOW startupInfo, out Win32.PROCESS_INFORMATION processInformation);
}
