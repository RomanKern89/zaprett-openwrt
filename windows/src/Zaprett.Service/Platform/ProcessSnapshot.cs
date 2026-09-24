using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zaprett.Service.Platform;

/// <summary>A running process: pid, name, full exe path (null when it cannot be read) and whether WinDivert.dll is loaded
/// in it — the one sign of a WinDivert user that does not depend on names: the driver service ("WinDivert") is shared,
/// and a second program just opens the driver the first one loaded (its ImagePath then points to the first program).</summary>
public sealed record ProcessEntry(int Pid, string Name, string? Path, bool UsesWinDivert);

/// <summary>
/// Processes of the system with their exe paths and WinDivert use, cached for a few seconds (status and the conflict
/// scan both ask, any user may call them). The walk uses QueryFullProcessImageName and EnumProcessModulesEx directly:
/// Process.MainModule / Process.Modules build full module objects and took 11–12 s for all processes on a workstation.
/// Programs under the Windows folder are not searched for WinDivert.dll.
/// </summary>
public static partial class ProcessSnapshot
{
    public const string WinDivertModule = "WinDivert.dll";
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(15);
    private static readonly object Lock = new();
    private static readonly string WindowsDir =
        System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + System.IO.Path.DirectorySeparatorChar;
    private static (DateTime At, IReadOnlyList<ProcessEntry> List)? _cached;

    public static IReadOnlyList<ProcessEntry> Get()
    {
        lock (Lock)
        {
            if (_cached is { } c && DateTime.UtcNow - c.At < CacheTime)
                return c.List;
        }
        var list = Take();
        lock (Lock)
            _cached = (DateTime.UtcNow, list);
        return list;
    }

    public static IReadOnlyList<ProcessEntry> Take()
    {
        var list = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string? path = null;
                bool divert = false;
                using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)p.Id);
                using var hq = h.IsInvalid ? OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)p.Id) : null;
                var handle = h.IsInvalid ? hq! : h;
                if (!handle.IsInvalid)
                {
                    path = ImagePath(handle);
                    // WinDivert programs (winws, GoodbyeDPI, …) ship WinDivert.dll next to their exe: only those folders
                    // are candidates, and the loaded module confirms (walking every process's modules took 3+ s)
                    if (!h.IsInvalid && path is not null && !path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase) &&
                        HasWinDivertBeside(path))
                        divert = HasModule(h, WinDivertModule);
                }
                list.Add(new ProcessEntry(p.Id, p.ProcessName, path, divert));
            }
        }
        return list;
    }

    internal static bool HasWinDivertBeside(string exePath)
    {
        try
        {
            return System.IO.Path.GetDirectoryName(exePath) is { } dir && File.Exists(System.IO.Path.Combine(dir, WinDivertModule));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool HasModule(Process p, string module)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)p.Id);
        return !h.IsInvalid && HasModule(h, module);
    }

    private static unsafe bool HasModule(SafeProcessHandle h, string module)
    {
        var mods = new nint[1024];
        fixed (nint* pm = mods)
        {
            if (!EnumProcessModulesEx(h, pm, mods.Length * IntPtr.Size, out int needed, LIST_MODULES_ALL))
                return false;
            int count = Math.Min(needed / IntPtr.Size, mods.Length);
            char* name = stackalloc char[260];
            for (int i = 0; i < count; i++)
            {
                int len = GetModuleBaseNameW(h, mods[i], name, 260);
                if (len > 0 && new ReadOnlySpan<char>(name, len).Equals(module, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Full path of a process's exe, or null when it cannot be opened.</summary>
    public static string? PathOf(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        return h.IsInvalid ? null : ImagePath(h);
    }

    private static unsafe string? ImagePath(SafeProcessHandle h)
    {
        char* buf = stackalloc char[1024];
        int size = 1024;
        return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
    }

    /// <summary>WinDivert users outside our install folder.</summary>
    public static IReadOnlyList<ProcessEntry> ForeignWinDivertUsers(IEnumerable<ProcessEntry> processes, string installDir)
    {
        string ours = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(installDir)) + System.IO.Path.DirectorySeparatorChar;
        return processes.Where(p => p.UsesWinDivert && !(p.Path?.StartsWith(ours, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint LIST_MODULES_ALL = 0x03;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* name, ref int size);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumProcessModulesEx(SafeProcessHandle process, nint* modules, int cb, out int needed, uint filter);

    [LibraryImport("psapi.dll", SetLastError = true)]
    private static unsafe partial int GetModuleBaseNameW(SafeProcessHandle process, nint module, char* name, int size);
}
