using System.Globalization;
using System.Text.RegularExpressions;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>A range of ports: [Start, Start + Count - 1].</summary>
public sealed record PortRange(int Start, int Count)
{
    public int End => Start + Count - 1;
    public bool Overlaps(int from, int to) => Start <= to && End >= from;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Start}-{End}");
}

/// <summary>The dynamic (ephemeral) TCP port ranges of Windows, IPv4 and IPv6; null when they cannot be read.</summary>
public interface IDynamicPortRanges
{
    Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="IDynamicPortRanges"/> from <c>netsh int ipv4|ipv6 show dynamicport tcp</c>. The output is localized
/// ("Start Port : 49152 / Number of Ports : 16384", "Начальный порт : 49152 / Число портов : 16384"): the first two
/// numbers after a colon are the start and the count.
/// </summary>
public sealed partial class NetshDynamicPorts(IProcessRunner runner, string? netshPath = null) : IDynamicPortRanges
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _netsh = netshPath ?? Path.Combine(Environment.SystemDirectory, "netsh.exe");

    [GeneratedRegex(@":\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NumberAfterColon();

    public static PortRange? Parse(string output)
    {
        var numbers = NumberAfterColon().Matches(output).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        if (numbers.Count < 2 || numbers[0] is < 1 or > 65535 || numbers[1] < 1 || numbers[0] + numbers[1] - 1 > 65535)
            return null;
        return new PortRange(numbers[0], numbers[1]);
    }

    public async Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct)
    {
        var ranges = new List<PortRange>();
        foreach (var family in new[] { "ipv4", "ipv6" })
        {
            var r = await runner.RunAsync(_netsh, ["int", family, "show", "dynamicport", "tcp"], Timeout, ct).ConfigureAwait(false);
            if (r.TimedOut || r.ExitCode != 0 || Parse(r.StdOut) is not { } range)
                return null;
            ranges.Add(range);
        }
        return ranges;
    }
}

/// <summary>
/// <see cref="IDynamicPortRanges"/> from WMI root\StandardCimv2 MSFT_NetTCPSetting (typed values, no text parsing): the
/// dynamic port range of every TCP setting template (Internet, Datacenter, Compat, …custom) that has one.
/// </summary>
public sealed class WmiDynamicPorts(ILog log) : IDynamicPortRanges
{
    public Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT SettingName, DynamicPortRangeStartPort, DynamicPortRangeNumberOfPorts FROM MSFT_NetTCPSetting");
            var list = new List<PortRange>();
            foreach (var o in s.Get())
            {
                using (o)
                {
                    if (o["DynamicPortRangeStartPort"] is { } start && o["DynamicPortRangeNumberOfPorts"] is { } count)
                    {
                        int st = Convert.ToInt32(start, CultureInfo.InvariantCulture), n = Convert.ToInt32(count, CultureInfo.InvariantCulture);
                        if (st is >= 1 and <= 65535 && n >= 1 && st + n - 1 <= 65535)
                            list.Add(new PortRange(st, n));
                    }
                }
            }
            return list.Count > 0 ? (IReadOnlyList<PortRange>?)list.Distinct().ToList() : null;
        }
        catch (Exception e) when (e is System.Management.ManagementException or System.Runtime.InteropServices.COMException
                                      or UnauthorizedAccessException)
        {
            log.Warn("dynamic ports: MSFT_NetTCPSetting: " + e.Message);
            return null;
        }
    }, ct);
}

/// <summary>Union of several sources; null only when none of them could say anything. Any overlap in any source
/// counts, so a range set for one IP family only (netsh int ipv6 set dynamicport) is not missed.</summary>
public sealed class CombinedDynamicPorts(params IDynamicPortRanges[] sources) : IDynamicPortRanges
{
    public async Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct)
    {
        List<PortRange>? all = null;
        foreach (var src in sources)
        {
            if (await src.GetTcpAsync(ct).ConfigureAwait(false) is { } r)
                (all ??= []).AddRange(r);
        }
        return all?.Distinct().ToList();
    }
}
