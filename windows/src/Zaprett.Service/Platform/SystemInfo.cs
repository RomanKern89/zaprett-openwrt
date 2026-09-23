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
public sealed record ServiceEntry(string Name, string DisplayName, bool Running, string? ImagePath);

/// <summary><see cref="ISystemInfo"/> from the registry, WMI and the service control manager (read only).</summary>
public sealed class SystemInfo(IPaths paths, ILog log) : ISystemInfo
{
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
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (var o in s.Get())
                if (o["SecurityServicesRunning"] is uint[] running)
                    return running.Contains(2u);
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            log.Warn("systeminfo: Win32_DeviceGuard: " + e.Message);
        }
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
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT AMServiceEnabled, RealTimeProtectionEnabled FROM MSFT_MpComputerStatus");
            foreach (var o in s.Get())
                return new JsonObject
                {
                    ["enabled"] = o["AMServiceEnabled"] as bool?,
                    ["realtime"] = o["RealTimeProtectionEnabled"] as bool?,
                };
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            log.Warn("systeminfo: MSFT_MpComputerStatus: " + e.Message);
        }
        return null;
    }

    public Task<JsonObject> GetWinDivertAsync(CancellationToken ct) => Task.Run(() =>
    {
        var drivers = WinDivertDrivers();
        var ours = drivers.Where(d => IsOurs(d.ImagePath)).ToList();
        string sys = Path.Combine(paths.EngineDir, "WinDivert64.sys");
        string? version = File.Exists(sys) ? FileVersionInfo.GetVersionInfo(sys).FileVersion : null;
        return new JsonObject
        {
            ["loaded"] = ours.Any(d => d.Running),
            ["version"] = version,
            ["foreign"] = new JsonArray(drivers.Where(d => !IsOurs(d.ImagePath)).Select(d => (JsonNode)d.Name).ToArray()),
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
                    list.Add(new ServiceEntry(sc.ServiceName, sc.DisplayName, sc.Status == ServiceControllerStatus.Running,
                        k?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string));
                }
                catch (InvalidOperationException)
                {
                    // service removed while listing
                }
            }
        }
        return list;
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
