using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// <see cref="IDnsControl"/> by ARCHITECTURE-WIN §12 S-6 (SPIKE-M1 §6). Windows 11: every connected adapter gets the
/// Cloudflare servers (known DoH template, no UDP fallback) and its own flag
/// <c>Dnscache\InterfaceSpecificParameters\{guid}\DohInterfaceSettings\Doh\&lt;ip&gt;\DohFlags = 1</c> — the global DoH
/// entries and other adapters stay as they were. Before the change the full state is saved to run\dns-backup.json
/// (adapter servers and DHCP/static, the DoH entries of our servers with their <c>Flags</c> value, the adapter Doh
/// subkeys), and disabling returns exactly to it. Windows 10: not_supported (no built-in DoH).
/// Only IPv4 is switched (what the spike verified); an adapter that also has IPv6 DNS servers is reported as not
/// encrypted (<c>ipv6_plain</c>) — the IPv6 variant needs its own check on a VM.
/// </summary>
public sealed partial class DnsControl(IProcessRunner runner, IPaths paths, ILog log, int? osBuild = null) : IDnsControl
{
    public const int Windows11Build = 22000;
    public const string DohTemplate = "https://cloudflare-dns.com/dns-query";
    public static readonly IReadOnlyList<string> DohServers = ["1.1.1.1", "1.0.0.1"];
    private const string DohKeyRoot = @"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";
    private const string WellKnownRoot = @"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohWellKnownServers";
    private const string TcpipInterfaces = @"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StatusCacheTime = TimeSpan.FromSeconds(15);

    private readonly int _build = osBuild ?? Environment.OSVersion.Version.Build;
    private JsonObject? _cached;
    private DateTime _cachedAt;

    private string BackupFile => Path.Combine(paths.RunDir, "dns-backup.json");
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public bool Supported => _build >= Windows11Build;

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    /// <summary>argv for powershell.exe running a script (-EncodedCommand: no quoting of the script text at all).</summary>
    public static IReadOnlyList<string> PowerShellArgs(string script) =>
    [
        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
        Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'\n" + script)),
    ];

    /// <summary>Everything S-6 says to save: connected physical adapters (index, guid, IPv4/IPv6 servers, DHCP or
    /// static, Doh subkeys with DohFlags), the DoH entries of our servers and whether they carry a Flags value.</summary>
    public static string StatusScript => $$"""
        $ErrorActionPreference = 'Stop'
        $ours = @({{string.Join(",", DohServers.Select(s => $"'{s}'"))}})
        $doh = @(Get-DnsClientDohServerAddress | ForEach-Object { [pscustomobject]@{ server = $_.ServerAddress; template = $_.DohTemplate; fallback = [bool]$_.AllowFallbackToUdp; auto = [bool]$_.AutoUpgrade } })
        $wk = @($ours | ForEach-Object { $k = '{{WellKnownRoot}}\' + $_; [pscustomobject]@{ server = $_; key = (Test-Path $k); flags = ($null -ne (Get-ItemProperty -Path $k -Name Flags -ErrorAction SilentlyContinue)) } })
        $ifs = @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object {
            $g = $_.InterfaceGuid
            $v4 = @((Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4).ServerAddresses)
            $v6 = @((Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv6).ServerAddresses)
            $ns = (Get-ItemProperty -Path ('{{TcpipInterfaces}}\' + $g) -Name NameServer -ErrorAction SilentlyContinue).NameServer
            $dk = '{{DohKeyRoot}}\' + $g + '\DohInterfaceSettings\Doh'
            $flags = @(if (Test-Path $dk) { Get-ChildItem $dk | ForEach-Object { [pscustomobject]@{ server = $_.PSChildName; flags = [long](Get-ItemProperty -Path $_.PSPath -Name DohFlags -ErrorAction SilentlyContinue).DohFlags } } })
            [pscustomobject]@{ index = [int]$_.ifIndex; guid = $g; name = $_.Name; servers = $v4; servers6 = $v6; dhcp = [string]::IsNullOrEmpty($ns); doh_flags = $flags } })
        [pscustomobject]@{ doh = $doh; well_known = $wk; interfaces = $ifs } | ConvertTo-Json -Depth 5 -Compress
        """;

    /// <summary>Enable: a DoH entry for each of our servers that has none (no fallback, no auto-upgrade — the adapter
    /// flag does the work), then per adapter the Doh\&lt;ip&gt; subkeys with DohFlags=1 and the servers themselves.</summary>
    public static string EnableScript(IEnumerable<(int Index, string Guid)> adapters, IEnumerable<string> missingDohEntries)
    {
        var sb = new StringBuilder("$ErrorActionPreference = 'Stop'\n");
        foreach (var server in missingDohEntries)
        {
            ValidateIPv4(server);
            sb.Append(CultureInfo.InvariantCulture,
                $"Add-DnsClientDohServerAddress -ServerAddress '{server}' -DohTemplate '{DohTemplate}' -AllowFallbackToUdp $False -AutoUpgrade $False | Out-Null\n");
        }
        string list = string.Join(",", DohServers.Select(s => $"'{s}'"));
        foreach (var (index, guid) in adapters)
        {
            ValidateGuid(guid);
            foreach (var server in DohServers)
            {
                string key = $@"{DohKeyRoot}\{guid}\DohInterfaceSettings\Doh\{server}";
                sb.Append(CultureInfo.InvariantCulture, $"New-Item -Path '{key}' -Force | Out-Null\n")
                  .Append(CultureInfo.InvariantCulture, $"New-ItemProperty -Path '{key}' -Name DohFlags -PropertyType QWord -Value 1 -Force | Out-Null\n");
            }
            sb.Append(CultureInfo.InvariantCulture, $"Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses @({list})\n");
        }
        sb.Append("Clear-DnsClientCache\n");
        return sb.ToString();
    }

    /// <summary>Disable: back to exactly the saved state.</summary>
    public static string RestoreScript(JsonObject backup)
    {
        var sb = new StringBuilder("$ErrorActionPreference = 'Continue'\n");
        foreach (var i in AsArray(backup["interfaces"]))
        {
            int index = Int(i?["index"]);
            string guid = Str(i?["guid"]);
            ValidateGuid(guid);
            var hadFlags = AsArray(i?["doh_flags"]).Select(f => Str(f?["server"])).ToHashSet(StringComparer.Ordinal);
            foreach (var server in DohServers.Where(s => !hadFlags.Contains(s)))
                sb.Append(CultureInfo.InvariantCulture,
                    $"Remove-Item -Path '{DohKeyRoot}\\{guid}\\DohInterfaceSettings\\Doh\\{server}' -Recurse -ErrorAction SilentlyContinue\n");
            var servers = AsArray(i?["servers"]).Select(Str).ToList();
            if (Bool(i?["dhcp"]) || servers.Count == 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Set-DnsClientServerAddress -InterfaceIndex {index} -ResetServerAddresses\n");
            }
            else
            {
                servers.ForEach(ValidateIp);
                sb.Append(CultureInfo.InvariantCulture,
                    $"Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses @({string.Join(",", servers.Select(s => $"'{s}'"))})\n");
            }
        }
        var hadEntry = AsArray(backup["doh"]).Select(d => Str(d?["server"])).ToHashSet(StringComparer.Ordinal);
        foreach (var server in DohServers.Where(s => !hadEntry.Contains(s)))
            sb.Append(CultureInfo.InvariantCulture, $"Remove-DnsClientDohServerAddress -ServerAddress '{server}' -ErrorAction SilentlyContinue\n");
        foreach (var w in AsArray(backup["well_known"]))
        {
            string server = Str(w?["server"]);
            ValidateIPv4(server);
            if (Bool(w?["key"]) && !Bool(w?["flags"]))
                sb.Append(CultureInfo.InvariantCulture,
                    $"Remove-ItemProperty -Path '{WellKnownRoot}\\{server}' -Name Flags -ErrorAction SilentlyContinue\n");
        }
        sb.Append("Clear-DnsClientCache\n");
        return sb.ToString();
    }

    /// <summary>Encrypted = every connected adapter uses only our servers, each with DohFlags=1 on the adapter and a
    /// DoH entry, and has no plain IPv6 DNS server.</summary>
    public static JsonObject Evaluate(JsonObject state)
    {
        var doh = new Dictionary<string, (string Template, bool Fallback)>(StringComparer.Ordinal);
        foreach (var d in AsArray(state["doh"]))
            doh[Str(d?["server"])] = (Str(d?["template"]), d?["fallback"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb);
        bool any = false, all = true, ipv6Plain = false;
        string? provider = null;
        foreach (var i in AsArray(state["interfaces"]))
        {
            var servers = AsArray(i?["servers"]).Select(Str).Where(x => x.Length > 0).ToList();
            var flags = AsArray(i?["doh_flags"]).Where(f => Int(f?["flags"]) == 1).Select(f => Str(f?["server"])).ToHashSet(StringComparer.Ordinal);
            if (AsArray(i?["servers6"]).Count > 0)
                ipv6Plain = true;
            if (servers.Count == 0)
            {
                all = false;
                continue;
            }
            foreach (var s in servers)
            {
                if (flags.Contains(s) && doh.TryGetValue(s, out var t) && t.Template.Length > 0)
                {
                    any = true;
                    provider ??= Uri.TryCreate(t.Template, UriKind.Absolute, out var u) ? u.Host : t.Template;
                }
                else
                {
                    all = false;
                }
            }
        }
        bool encrypted = any && all && !ipv6Plain;
        var r = new JsonObject
        {
            ["encrypted"] = encrypted,
            ["provider"] = encrypted ? provider : null,
            ["mode"] = encrypted ? "doh" : "system",
            ["supported"] = true,
        };
        if (ipv6Plain)
            r["ipv6_plain"] = true;
        return r;
    }

    /// <summary>Cached for a few seconds: every read runs PowerShell as SYSTEM, and any user may ask.</summary>
    public async Task<JsonObject> GetStatusAsync(CancellationToken ct)
    {
        if (!Supported)
            return new JsonObject { ["encrypted"] = false, ["provider"] = null, ["mode"] = "system", ["supported"] = false };
        if (Volatile.Read(ref _cached) is { } c && DateTime.UtcNow - _cachedAt < StatusCacheTime)
            return (JsonObject)c.DeepClone();
        var result = Evaluate(await ReadStateAsync(ct).ConfigureAwait(false));
        _cachedAt = DateTime.UtcNow;
        Volatile.Write(ref _cached, (JsonObject)result.DeepClone());
        return result;
    }

    public async Task<JsonObject> SetupAsync(bool enable, CancellationToken ct)
    {
        if (!Supported)
            return new JsonObject { ["ok"] = false, ["error"] = "not_supported", ["message"] = "Built-in DNS-over-HTTPS needs Windows 11" };
        Volatile.Write(ref _cached, null);
        try
        {
            return enable ? await EnableAsync(ct).ConfigureAwait(false) : await DisableAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or IOException or ArgumentException
                                      or System.Text.Json.JsonException)
        {
            log.Error("dns setup failed: " + e.Message);
            return new JsonObject { ["ok"] = false, ["error"] = "dns_failed", ["message"] = e.Message };
        }
    }

    private async Task<JsonObject> EnableAsync(CancellationToken ct)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        if (Evaluate(state)["encrypted"]!.GetValue<bool>())
            return new JsonObject { ["ok"] = true, ["changed"] = false };
        var adapters = AsArray(state["interfaces"]).Select(i => (Int(i?["index"]), Str(i?["guid"]))).ToList();
        if (adapters.Count == 0)
            return new JsonObject { ["ok"] = false, ["error"] = "no_adapter", ["message"] = "No connected network adapter" };
        // the first saved state is the one to return to: a second "enable" must not overwrite it
        if (!File.Exists(BackupFile))
        {
            Directory.CreateDirectory(paths.RunDir);
            await File.WriteAllTextAsync(BackupFile, state.ToJsonString(), ct).ConfigureAwait(false);
        }
        var have = AsArray(state["doh"]).Select(d => Str(d?["server"])).ToHashSet(StringComparer.Ordinal);
        await RunScriptAsync(EnableScript(adapters, DohServers.Where(s => !have.Contains(s))), ct).ConfigureAwait(false);
        log.Info($"dns: DNS-over-HTTPS enabled on {adapters.Count} adapter(s)");
        return new JsonObject { ["ok"] = true, ["changed"] = true };
    }

    private async Task<JsonObject> DisableAsync(CancellationToken ct)
    {
        if (!File.Exists(BackupFile))
            return new JsonObject { ["ok"] = true, ["changed"] = false };
        var backup = JsonNode.Parse(await File.ReadAllTextAsync(BackupFile, ct).ConfigureAwait(false)) as JsonObject
            ?? throw new FormatException("dns-backup.json is not an object");
        await RunScriptAsync(RestoreScript(backup), ct).ConfigureAwait(false);
        File.Delete(BackupFile);
        log.Info("dns: adapter DNS settings restored");
        return new JsonObject { ["ok"] = true, ["changed"] = true };
    }

    private async Task<JsonObject> ReadStateAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync(PowerShell, PowerShellArgs(StatusScript), Timeout, ct).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0)
            throw new InvalidOperationException($"DNS status failed ({r.ExitCode}): {r.StdErr.Trim()}");
        return JsonNode.Parse(r.StdOut.Trim()) as JsonObject ?? throw new FormatException("unexpected DNS status output");
    }

    private async Task RunScriptAsync(string script, CancellationToken ct)
    {
        var r = await runner.RunAsync(PowerShell, PowerShellArgs(script), Timeout, ct).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell failed ({r.ExitCode}): {r.StdErr.Trim()}");
    }

    private static void ValidateIPv4(string s)
    {
        if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != s)
            throw new ArgumentException("not an IPv4 address: " + s);
    }

    /// <summary>Only canonical IP literals reach a script (nothing else can carry quotes or code).</summary>
    private static void ValidateIp(string s)
    {
        if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
            (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) || ip.ToString() != s)
            throw new ArgumentException("not an IP address: " + s);
    }

    private static void ValidateGuid(string s)
    {
        if (!GuidRegex().IsMatch(s))
            throw new ArgumentException("not an interface GUID: " + s);
    }

    private static JsonArray AsArray(JsonNode? n) => n switch
    {
        JsonArray a => a,
        JsonObject o => [o.DeepClone()],   // ConvertTo-Json writes a single element without brackets
        JsonValue v => [v.DeepClone()],
        _ => [],
    };

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? (int)l : n is JsonValue d && d.TryGetValue<double>(out var x) ? (int)x : 0;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
