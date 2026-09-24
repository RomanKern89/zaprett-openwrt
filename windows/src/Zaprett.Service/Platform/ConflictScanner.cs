using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>What the scanner looked at; collected from the system, or built by tests.</summary>
public sealed record ConflictSnapshot(
    IReadOnlyList<ServiceEntry> Services,
    IReadOnlyList<ProcessEntry> Processes,
    IReadOnlyList<(string Name, string Description, NetworkInterfaceType Type)> ActiveAdapters,
    IReadOnlyList<string> Proxies,
    string InstallDir);

/// <summary>
/// <see cref="IConflictScanner"/> (ARCHITECTURE-WIN §8): {id, name, severity: block|warn|info, kind, detail, fix} and,
/// by kind, {service} (a Windows service), {pid, path} (a process), {path} (a driver). The advice depends on the kind:
/// a service is stopped and disabled by its name, a process is closed or ended by its exe path and pid, a driver is
/// named by its ImagePath. Our own folder is excluded by path, never by name (a foreign winws.exe is still winws.exe).
/// </summary>
public sealed class ConflictScanner(IPaths paths, ILog log) : IConflictScanner
{
    private sealed record Rule(string Id, string Name, string Severity, string[] ServiceMarks, string[] ProcessMarks, string Why);

    // marks are case-insensitive substrings of the service name/display name or the process name
    private static readonly Rule[] Rules =
    [
        new("goodbyedpi", "GoodbyeDPI", "block", ["GoodbyeDPI"], ["goodbyedpi"], "two DPI bypass programs on one PC break each other"),
        new("zapret", "zapret / winws (another installation)", "block", ["zapret", "winws"], ["winws"],
            "two DPI bypass programs on one PC break each other"),
        new("adguard", "AdGuard", "warn", ["Adguard"], ["adguard"], "AdGuard filters traffic with its own driver"),
        new("killer", "Killer Network Service", "warn", ["Killer"], ["KillerNetworkService", "Killer"],
            "Killer prioritization can drop modified packets"),
        new("intel-cns", "Intel Connectivity Network Service", "warn", ["Intel(R) Connectivity Network", "Intel Connectivity Network"], [],
            "it can interfere with modified packets"),
        new("checkpoint", "Check Point VPN / Endpoint", "warn", ["Check Point", "TracSrvWrapper", "EPWD"], ["TracSrvWrapper"],
            "the Check Point client filters traffic"),
        new("smartbyte", "SmartByte", "warn", ["SmartByte"], ["SmartByte"], "SmartByte shapes traffic"),
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

        // drivers: a WinDivert driver service that is not our engine\WinDivert64.sys (S-8/§12.1)
        string ourDriver = Path.Combine(s.InstallDir, "engine", "WinDivert64.sys");
        foreach (var d in s.Services.Where(x => x.Name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase)))
        {
            string? image = d.ImagePath is { } ip ? SystemInfo.NormalizeImagePath(ip) : null;
            if (image is not null && string.Equals(image, ourDriver, StringComparison.OrdinalIgnoreCase))
                continue;
            var item = d.Running
                ? Item("foreign_windivert", "WinDivert (" + d.Name + ")", "block", "driver",
                    $"Another program's WinDivert driver is loaded (service \"{d.Name}\"): {image ?? "?"}",
                    $"Close the program whose folder holds {image ?? "this driver"} (GoodbyeDPI, another zapret…), then restart zaprett.")
                : Item("windivert_registered", "WinDivert (" + d.Name + ")", "info", "driver",
                    $"A WinDivert driver of another program is registered (not loaded): {image ?? "?"}",
                    "Nothing to do while it is not loaded; remove the program that installed it if problems appear.");
            item["service"] = d.Name;
            item["path"] = image;
            items.Add(item);
        }

        // processes that use WinDivert outside our folder, whatever they are called (a foreign winws shares the
        // driver our winws loaded, so the driver service alone does not show it)
        var reported = new HashSet<int>();
        foreach (var p in ProcessSnapshot.ForeignWinDivertUsers(s.Processes, s.InstallDir))
        {
            items.Add(ProcessItem("foreign_windivert_user", p.Name + " (WinDivert)", "block", p,
                "uses WinDivert — it intercepts the same traffic as zaprett"));
            reported.Add(p.Pid);
        }

        foreach (var r in Rules)
        {
            var svc = s.Services.FirstOrDefault(x => !x.Name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase) && !IsOurService(x, ourDir) &&
                r.ServiceMarks.Any(m => x.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || x.DisplayName.Contains(m, StringComparison.OrdinalIgnoreCase)));
            if (svc is not null)
            {
                string severity = r.Severity == "block" && !svc.Running ? "warn" : r.Severity;
                var item = Item(r.Id, r.Name, severity, "service",
                    $"Service \"{svc.Name}\" ({svc.DisplayName}) {(svc.Running ? "is running" : "is installed")}: {r.Why}",
                    $"Stop and disable the service \"{svc.Name}\" (services.msc, or as administrator: sc stop \"{svc.Name}\" && sc config \"{svc.Name}\" start= disabled).");
                item["service"] = svc.Name;
                if (svc.ImagePath is { } sp)
                    item["path"] = ServiceExe(sp);
                if (svc.Pid is { } spid)
                    item["pid"] = spid;
                items.Add(item);
            }
            foreach (var p in s.Processes.Where(p => !reported.Contains(p.Pid) && !IsOurPath(p.Path, ourDir) &&
                                                     r.ProcessMarks.Any(m => p.Name.Contains(m, StringComparison.OrdinalIgnoreCase))))
            {
                // the process of a service already reported is not a second finding
                if (svc?.ImagePath is { } image && p.Path is not null &&
                    SystemInfo.NormalizeImagePath(image).Contains(p.Path, StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(ProcessItem(r.Id, r.Name, r.Severity, p, r.Why));
                reported.Add(p.Pid);
            }
        }

        foreach (var a in s.ActiveAdapters)
        {
            bool vpn = a.Type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ||
                       VpnMarks.Any(m => a.Description.Contains(m, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (vpn)
                items.Add(Item("vpn:" + a.Name, "VPN: " + a.Name, "info", "adapter",
                    $"Active VPN adapter \"{a.Description}\": traffic inside the VPN does not need the bypass",
                    "Nothing to do; if sites break only with the VPN on, turn one of them off."));
        }

        foreach (var p in s.Proxies)
            items.Add(Item("proxy", "System proxy", "info", "proxy", "A proxy is set: " + p,
                "Traffic through a proxy goes to the proxy, not to the site: the bypass does not apply to it."));

        return items;
    }

    /// <summary>The exe of a service ImagePath (quoted or not, with or without arguments).</summary>
    public static string ServiceExe(string imagePath)
    {
        string s = imagePath.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return SystemInfo.NormalizeImagePath(end > 0 ? s[1..end] : s[1..]);
        }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return SystemInfo.NormalizeImagePath(exe > 0 ? s[..(exe + 4)] : s);
    }

    private static JsonObject ProcessItem(string id, string name, string severity, ProcessEntry p, string why)
    {
        string exe = p.Path ?? p.Name + ".exe (path unknown)";
        var item = Item(id, name, severity, "process",
            $"Process {p.Name} (pid {p.Pid}) is running from {exe}: {why}",
            $"Close the program that started {exe}, or end the process {p.Pid} (Task Manager, or as administrator: taskkill /PID {p.Pid} /F); " +
            "if it starts again, remove it from autostart.");
        item["pid"] = p.Pid;
        item["path"] = p.Path;
        return item;
    }

    // our own service is "zaprett" (contains "zapret"): never report it, wherever it is installed
    private static bool IsOurService(ServiceEntry s, string ourDir) =>
        s.Name.Equals("zaprett", StringComparison.OrdinalIgnoreCase) || IsOurPath(s.ImagePath, ourDir);

    private static bool IsOurPath(string? path, string ourDir) =>
        path is not null && SystemInfo.NormalizeImagePath(path).Contains(ourDir, StringComparison.OrdinalIgnoreCase);

    private static JsonObject Item(string id, string name, string severity, string kind, string detail, string fix) =>
        new() { ["id"] = id, ["name"] = name, ["severity"] = severity, ["kind"] = kind, ["detail"] = detail, ["fix"] = fix };

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
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (n.Name, n.Description, n.NetworkInterfaceType)).ToList();
        return new ConflictSnapshot(services, ProcessSnapshot.Get(), adapters, Proxies(), paths.InstallDir);
    }

    /// <summary>The per-user proxy of every loaded user profile.</summary>
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
