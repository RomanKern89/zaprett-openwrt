using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Zaprett.Ipc;

/// <summary>
/// Who owns the pipe we connected to. Before the service starts, any user could create \\.\pipe\zaprett and pose as
/// the service (read our arguments, show a false status). The server is trusted only if it is the process of the
/// Windows service "zaprett", or — for development in console mode — a process of the same user as the client
/// (such a process already has everything the client has).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class PipeServerCheck
{
    public const string ServiceName = "zaprett";

    /// <summary>The decision, separated for tests.</summary>
    public static bool IsTrusted(int serverPid, int? servicePid, SecurityIdentifier? serverUser, SecurityIdentifier clientUser) =>
        (servicePid is { } sp && sp == serverPid && sp != 0) || (serverUser is not null && serverUser.Equals(clientUser));

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> when the pipe server is neither the service nor our user.</summary>
    public static void Verify(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid))
            throw new IOException("GetNamedPipeServerProcessId failed: " + Marshal.GetLastPInvokeError());
        using var me = WindowsIdentity.GetCurrent();
        if (!IsTrusted((int)pid, ServicePid(), ProcessUser((int)pid), me.User!))
            throw new UnauthorizedAccessException($"the pipe is served by process {pid}, which is not the zaprett service");
    }

    /// <summary>PID of the running service, from the service control manager; null when it is not running.</summary>
    public static int? ServicePid()
    {
        nint scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == 0)
            return null;
        try
        {
            nint svc = OpenServiceW(scm, ServiceName, SERVICE_QUERY_STATUS);
            if (svc == 0)
                return null;
            try
            {
                var st = new SERVICE_STATUS_PROCESS();
                return QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref st, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _) && st.dwProcessId != 0
                    ? (int)st.dwProcessId
                    : null;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>User of a process, or null when its token cannot be read (other users' and system processes).</summary>
    public static SecurityIdentifier? ProcessUser(int pid)
    {
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process.IsInvalid)
            return null;
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            return null;
        using (token)
        {
            try
            {
                using var id = new WindowsIdentity(token.DangerousGetHandle());
                return id.User;
            }
            catch (Exception e) when (e is ArgumentException or System.Security.SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int SC_STATUS_PROCESS_INFO = 0;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode,
            dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(nint service, int infoLevel, ref SERVICE_STATUS_PROCESS buffer, int size, out int needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
