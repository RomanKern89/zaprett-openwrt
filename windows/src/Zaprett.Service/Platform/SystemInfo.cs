using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Zaprett.Core.Platform;
using Zaprett.Service.Native;

namespace Zaprett.Service.Platform;

/// <summary>A kernel driver or service as seen by the service control manager.</summary>
/// <summary>A service or kernel driver as the service control manager sees it; Pid is the process of a running
/// Win32 service (null for drivers, stopped services, or when it cannot be read).</summary>
public sealed record ServiceEntry(string Name, string DisplayName, bool Running, string? ImagePath, int? Pid = null);

/// <summary><see cref="ISystemInfo"/> from the registry, WMI and the service control manager (read only).</summary>
public sealed class SystemInfo(IPaths paths, ILog log) : ISystemInfo
{
    // WMI providers of Defender / Device Guard can be busy for a while (after an install): not a WARN each time
    private readonly TransientRead<bool?> _hvci = new("systeminfo: Win32_DeviceGuard", log);
    private readonly TransientRead<DefenderState?> _defender = new("systeminfo: MSFT_MpComputerStatus", log);

    private sealed record DefenderState(bool? Enabled, bool? Realtime);

    private static bool IsWmiFailure(Exception e) => e is ManagementException or COMException or UnauthorizedAccessException;

    public Task<JsonObject> GetPlatformAsync(CancellationToken ct) => Task.Run(() =>
    {
        int build = Environment.OSVersion.Version.Build;
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        object? ubr = cv?.GetValue("UBR");
        return new JsonObject
        {
            ["os"] = build >= DnsControl.Windows11Build ? "Windows 11" : "Windows 10",
            ["build"] = ubr is int u ? $"{build}.{u}" : build.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["version"] = cv?.GetValue("DisplayVersion") as string,
            ["edition"] = cv?.GetValue("EditionID") as string,
            ["arch"] = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ["hvci"] = Hvci(),
            ["smart_app_control"] = SmartAppControl(),
            ["defender"] = Defender(),
        };
    }, ct);

    /// <summary>Memory integrity (HVCI) running, by Win32_DeviceGuard; registry as a fallback. Null = unknown.</summary>
    private bool? Hvci()
    {
        var wmi = _hvci.Get(() =>
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (var o in s.Get())
                if (o["SecurityServicesRunning"] is uint[] running)
                    return running.Contains(2u);
            return null;
        }, IsWmiFailure);
        if (wmi is not null)
            return wmi;
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return k?.GetValue("Enabled") is int v ? v == 1 : null;
    }

    /// <summary>Smart App Control: "on", "evaluation", "off", or null when the OS has none.</summary>
    private static string? SmartAppControl()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
        return k?.GetValue("VerifiedAndReputablePolicyState") switch
        {
            1 => "on",
            2 => "evaluation",
            0 => "off",
            _ => null,
        };
    }

    /// <summary>Microsoft Defender state: {enabled, realtime}; null when it cannot be read (third-party AV).</summary>
    private JsonObject? Defender()
    {
        var state = _defender.Get(() =>
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT AMServiceEnabled, RealTimeProtectionEnabled FROM MSFT_MpComputerStatus");
            foreach (var o in s.Get())
                return new DefenderState(o["AMServiceEnabled"] as bool?, o["RealTimeProtectionEnabled"] as bool?);
            return null;
        }, IsWmiFailure);
        // a new object every time: a JsonNode belongs to one parent
        return state is null ? null : new JsonObject { ["enabled"] = state.Enabled, ["realtime"] = state.Realtime };
    }

    public Task<JsonObject> GetWinDivertAsync(CancellationToken ct) => Task.Run(() =>
    {
        var drivers = WinDivertDrivers();
        var ours = drivers.Where(d => IsOurs(d.ImagePath)).ToList();
        var foreignDrivers = drivers.Where(d => !IsOurs(d.ImagePath)).ToList();
        // a foreign program that opens the driver our winws already loaded does not show in the driver service:
        // it is found by the WinDivert.dll in its process
        var users = ProcessSnapshot.ForeignWinDivertUsers(ProcessSnapshot.Get(), paths.InstallDir);
        string sys = Path.Combine(paths.EngineDir, "WinDivert64.sys");
        string? version = File.Exists(sys) ? FileVersionInfo.GetVersionInfo(sys).FileVersion : null;
        return new JsonObject
        {
            ["loaded"] = ours.Any(d => d.Running),
            ["version"] = version,
            // human-readable, one line per finding (kept a list of strings for older readers)
            ["foreign"] = new JsonArray(foreignDrivers.Select(d => (JsonNode)$"{d.Name}: {(d.ImagePath is { } ip ? NormalizeImagePath(ip) : "?")}")
                .Concat(users.Select(u => (JsonNode)$"{u.Name} (pid {u.Pid}): {u.Path ?? "?"}")).ToArray()),
            ["foreign_drivers"] = new JsonArray(foreignDrivers.Select(d => (JsonNode)new JsonObject
            {
                ["name"] = d.Name, ["path"] = d.ImagePath is { } ip ? NormalizeImagePath(ip) : null, ["running"] = d.Running,
            }).ToArray()),
            ["foreign_users"] = new JsonArray(users.Select(u => (JsonNode)new JsonObject
            {
                ["pid"] = u.Pid, ["name"] = u.Name, ["path"] = u.Path,
            }).ToArray()),
        };
    }, ct);

    // ours = exactly <engine>\WinDivert64.sys (§12.1); anything else, even inside our folder, is foreign
    private bool IsOurs(string? imagePath) =>
        imagePath is not null && string.Equals(NormalizeImagePath(imagePath), Path.Combine(paths.EngineDir, "WinDivert64.sys"),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Driver ImagePath to a plain path: strips quotes and the \??\ prefix, expands %SystemRoot%.</summary>
    public static string NormalizeImagePath(string imagePath)
    {
        string p = Environment.ExpandEnvironmentVariables(imagePath.Trim().Trim('"'));
        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
            p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p[12..]);
        return p;
    }

    /// <summary>Kernel driver services whose name starts with WinDivert.</summary>
    public static IReadOnlyList<ServiceEntry> WinDivertDrivers() =>
        ListServices(ServiceController.GetDevices()).Where(s => s.Name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase)).ToList();

    public static IReadOnlyList<ServiceEntry> ListServices(ServiceController[] controllers)
    {
        var list = new List<ServiceEntry>();
        foreach (var sc in controllers)
        {
            using (sc)
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + sc.ServiceName);
                    bool running = sc.Status == ServiceControllerStatus.Running;
                    list.Add(new ServiceEntry(sc.ServiceName, sc.DisplayName, running,
                        k?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                        running ? ServicePid(sc) : null));
                }
                catch (InvalidOperationException)
                {
                    // service removed while listing
                }
            }
        }
        return list;
    }

    /// <summary>PID of a running Win32 service (QueryServiceStatusEx with SERVICE_QUERY_STATUS only, which any user
    /// has — ServiceController.ServiceHandle asks for full access); null for drivers or when it cannot be read.</summary>
    public static int? ServicePid(ServiceController sc)
    {
        nint scm = Win32.OpenSCManagerW(null, null, Win32.SC_MANAGER_CONNECT);
        if (scm == 0)
            return null;
        try
        {
            nint svc = Win32.OpenServiceW(scm, sc.ServiceName, Win32.SERVICE_QUERY_STATUS);
            if (svc == 0)
                return null;
            try
            {
                var st = new Win32.SERVICE_STATUS_PROCESS();
                return Win32.QueryServiceStatusEx(svc, 0, ref st, Marshal.SizeOf<Win32.SERVICE_STATUS_PROCESS>(), out _) && st.dwProcessId != 0
                    ? (int)st.dwProcessId
                    : null;
            }
            finally
            {
                Win32.CloseServiceHandle(svc);
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }

    public long? MemoryAvailableMiB
    {
        get
        {
            var m = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
            return Win32.GlobalMemoryStatusEx(ref m) ? (long)(m.ullAvailPhys / (1024 * 1024)) : null;
        }
    }
}
