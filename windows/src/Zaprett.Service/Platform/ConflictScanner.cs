using System.Diagnostics;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>What the scanner looked at; collected from the system, or built by tests.</summary>
public sealed record ConflictSnapshot(
    IReadOnlyList<ServiceEntry> Services,
    IReadOnlyList<(string Name, string? Path)> Processes,
    IReadOnlyList<(string Name, string Description, NetworkInterfaceType Type)> ActiveAdapters,
    IReadOnlyList<string> Proxies,
    string InstallDir);

/// <summary><see cref="IConflictScanner"/> (ARCHITECTURE-WIN §8): {id, name, severity: block|warn|info, detail, fix}.</summary>
public sealed class ConflictScanner(IPaths paths, ILog log) : IConflictScanner
{
    private sealed record Rule(string Id, string Name, string Severity, string[] ServiceMarks, string[] ProcessMarks, string Fix);

    // marks are case-insensitive substrings of the service name/display name or the process name
    private static readonly Rule[] Rules =
    [
        new("goodbyedpi", "GoodbyeDPI", "block", ["GoodbyeDPI"], ["goodbyedpi"],
            "Stop and remove GoodbyeDPI: two DPI bypass programs on one PC break each other."),
        new("zapret", "zapret / winws (another installation)", "block", ["zapret", "winws"], ["winws"],
            "Stop the other zapret (for example the Flowseal service \"zapret\") and remove its service."),
        new("adguard", "AdGuard", "warn", ["Adguard"], ["adguard"],
            "AdGuard filters traffic with its own driver: turn off its network filtering or add an exception."),
        new("killer", "Killer Network Service", "warn", ["Killer"], ["KillerNetworkService", "Killer"],
            "Killer prioritization can drop modified packets: disable Killer Network Service."),
        new("intel-cns", "Intel Connectivity Network Service", "warn", ["Intel(R) Connectivity Network", "Intel Connectivity Network"], [],
            "Disable Intel Connectivity Network Service if sites still do not open."),
        new("checkpoint", "Check Point VPN / Endpoint", "warn", ["Check Point", "TracSrvWrapper", "EPWD"], ["TracSrvWrapper"],
            "Check Point client filters traffic: disconnect it or add an exception."),
        new("smartbyte", "SmartByte", "warn", ["SmartByte"], ["SmartByte"],
            "SmartByte shapes traffic: disable the SmartByte service."),
    ];

    private static readonly string[] VpnMarks = ["VPN", "WireGuard", "Wintun", "TAP-Windows", "OpenVPN", "Tailscale", "ZeroTier", "Hamachi", "AmneziaWG", "Outline"];

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(15);
    private JsonArray? _cached;
    private DateTime _cachedAt;

    /// <summary>Cached for a few seconds: a scan walks every process and service, and any user may ask.</summary>
    public async Task<JsonArray> ScanAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _cached) is { } c && DateTime.UtcNow - _cachedAt < CacheTime)
            return (JsonArray)c.DeepClone();
        var result = Evaluate(await Task.Run(Collect, ct).ConfigureAwait(false));
        _cachedAt = DateTime.UtcNow;
        Volatile.Write(ref _cached, (JsonArray)result.DeepClone());
        return result;
    }

    public static JsonArray Evaluate(ConflictSnapshot s)
    {
        var items = new JsonArray();
        string ourDir = Path.TrimEndingDirectorySeparator(s.InstallDir) + Path.DirectorySeparatorChar;

        // S-8/§12.1: foreign_windivert = a WinDivert driver is loaded and it is not our engine\WinDivert64.sys
        string ourDriver = Path.Combine(s.InstallDir, "engine", "WinDivert64.sys");
        foreach (var d in s.Services.Where(x => x.Name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase)))
        {
            if (d.ImagePath is { } ip && string.Equals(SystemInfo.NormalizeImagePath(ip), ourDriver, StringComparison.OrdinalIgnoreCase))
                continue;
            if (d.Running)
                items.Add(Item("foreign_windivert", "WinDivert (" + d.Name + ")", "block",
                    $"Another program's WinDivert driver is loaded: {d.ImagePath ?? "?"}",
                    "Close and remove GoodbyeDPI, another zapret or similar programs, then restart zaprett."));
            else
                items.Add(Item("windivert_registered", "WinDivert (" + d.Name + ")", "info",
                    $"A WinDivert driver of another program is registered (not loaded): {d.ImagePath ?? "?"}",
                    "Nothing to do while it is not loaded; remove the program that installed it if problems appear."));
        }

        foreach (var r in Rules)
        {
            var svc = s.Services.FirstOrDefault(x => !x.Name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase) && !IsOurService(x, ourDir) &&
                r.ServiceMarks.Any(m => x.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || x.DisplayName.Contains(m, StringComparison.OrdinalIgnoreCase)));
            var proc = s.Processes.Where(p => !IsOurPath(p.Path, ourDir))
                .Select(p => p.Name).FirstOrDefault(p => r.ProcessMarks.Any(m => p.Contains(m, StringComparison.OrdinalIgnoreCase)));
            if (svc is null && proc is null)
                continue;
            bool active = proc is not null || svc!.Running;
            string severity = r.Severity == "block" && !active ? "warn" : r.Severity;
            string detail = svc is not null
                ? $"service {svc.Name} ({svc.DisplayName}) {(svc.Running ? "is running" : "is installed")}"
                : $"process {proc} is running";
            items.Add(Item(r.Id, r.Name, severity, detail, r.Fix));
        }

        foreach (var a in s.ActiveAdapters)
        {
            bool vpn = a.Type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ||
                       VpnMarks.Any(m => a.Description.Contains(m, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (vpn)
                items.Add(Item("vpn:" + a.Name, "VPN: " + a.Name, "info",
                    $"Active VPN adapter \"{a.Description}\": traffic inside the VPN does not need the bypass",
                    "Nothing to do; if sites break only with the VPN on, turn one of them off."));
        }

        foreach (var p in s.Proxies)
            items.Add(Item("proxy", "System proxy", "info", "A proxy is set: " + p,
                "Traffic through a proxy goes to the proxy, not to the site: the bypass does not apply to it."));

        return items;
    }

    // our own service is "zaprett" (contains "zapret"): never report it, wherever it is installed
    private static bool IsOurService(ServiceEntry s, string ourDir) =>
        s.Name.Equals("zaprett", StringComparison.OrdinalIgnoreCase) || IsOurPath(s.ImagePath, ourDir);

    private static bool IsOurPath(string? path, string ourDir) =>
        path is not null && SystemInfo.NormalizeImagePath(path).Contains(ourDir, StringComparison.OrdinalIgnoreCase);

    private static JsonObject Item(string id, string name, string severity, string detail, string fix) =>
        new() { ["id"] = id, ["name"] = name, ["severity"] = severity, ["detail"] = detail, ["fix"] = fix };

    private ConflictSnapshot Collect()
    {
        var services = new List<ServiceEntry>();
        try
        {
            services.AddRange(SystemInfo.ListServices(ServiceController.GetServices()));
            services.AddRange(SystemInfo.ListServices(ServiceController.GetDevices()));
        }
        catch (InvalidOperationException e)
        {
            log.Warn("conflicts: services: " + e.Message);
        }
        var processes = new List<(string, string?)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string? path = null;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // protected or already gone
                }
                processes.Add((p.ProcessName, path));
            }
        }
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (n.Name, n.Description, n.NetworkInterfaceType)).ToList();
        return new ConflictSnapshot(services, processes, adapters, Proxies(), paths.InstallDir);
    }

    /// <summary>WinHTTP proxy (machine) and the per-user proxy of every loaded user profile.</summary>
    private List<string> Proxies()
    {
        var list = new List<string>();
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith("_Classes", StringComparison.Ordinal))
                    continue;
                using var k = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                if (k?.GetValue("ProxyEnable") is int en && en != 0 && k.GetValue("ProxyServer") is string server && server.Length > 0)
                    list.Add(server);
                else if (k?.GetValue("AutoConfigURL") is string pac && pac.Length > 0)
                    list.Add("PAC " + pac);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn("conflicts: proxy settings: " + e.Message);
        }
        return list.Distinct().ToList();
    }
}
