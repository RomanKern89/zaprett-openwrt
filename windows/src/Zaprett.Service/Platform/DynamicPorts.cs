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

/// <summary>Union of several sources, asked at the same time; null only when none of them could say anything. Any
/// overlap in any source counts, so a range set for one IP family only (netsh int ipv6 set dynamicport) is not missed.
/// A source gets <see cref="DefaultSourceTimeout"/>: after a fresh install WMI answered in 19.4 s (the Windows 10 test machine, D13), and the
/// engine start waits for this answer.</summary>
public sealed class CombinedDynamicPorts : IDynamicPortRanges
{
    public static readonly TimeSpan DefaultSourceTimeout = TimeSpan.FromSeconds(3);

    private readonly IDynamicPortRanges[] _sources;
    private readonly TimeSpan _timeout;
    private readonly ILog? _log;
    private readonly TimeProvider _time;

    public CombinedDynamicPorts(params IDynamicPortRanges[] sources) : this(DefaultSourceTimeout, null, sources)
    {
    }

    public CombinedDynamicPorts(TimeSpan sourceTimeout, ILog? log, params IDynamicPortRanges[] sources)
        : this(sourceTimeout, log, TimeProvider.System, sources)
    {
    }

    public CombinedDynamicPorts(TimeSpan sourceTimeout, ILog? log, TimeProvider time, params IDynamicPortRanges[] sources)
    {
        _sources = sources;
        _timeout = sourceTimeout;
        _log = log;
        _time = time;
    }

    public async Task<IReadOnlyList<PortRange>?> GetTcpAsync(CancellationToken ct)
    {
        using var limit = new CancellationTokenSource(_timeout, _time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        (IReadOnlyList<PortRange>? Ranges, string? Failure)[] results;
        try
        {
            results = await Task.WhenAll(_sources.Select(s => AskAsync(s, cts.Token, ct))).ConfigureAwait(false);
        }
        finally
        {
            // a source left behind at its time limit (netsh still running) gets the cancellation before the source of
            // it is disposed; the CancelAfter timer alone may not have fired yet
            cts.Cancel();
        }
        List<PortRange>? all = null;
        foreach (var r in results)
        {
            if (r.Ranges is not null)
                (all ??= []).AddRange(r.Ranges);
        }
        if (_sources.Length > 0)
            ReportOutcome(all is null, results.Where(r => r.Failure is not null).Select(r => r.Failure!).ToList());
        return all?.Distinct().ToList();
    }

    private async Task<(IReadOnlyList<PortRange>? Ranges, string? Failure)> AskAsync(IDynamicPortRanges source,
        CancellationToken limited, CancellationToken ct)
    {
        string name = source.GetType().Name;
        string? failure;
        try
        {
            // WaitAsync: a source that ignores the cancellation (a hung WMI call) is left behind, not waited for
            var r = await source.GetTcpAsync(limited).WaitAsync(_timeout, _time, ct).ConfigureAwait(false);
            if (r is not null)
                return (r, null);
            failure = $"{name} gave no ranges";
        }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            failure = $"{name} did not answer within {_timeout.TotalSeconds:0.#} s";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // one broken source (an unexpected value, a runner failure) must not lose the other's answer
            failure = $"{name} failed: {e.GetType().Name}: {e.Message}";
        }
        // one source missing while another answers is normal (a slow start after a boot): Info
        _log?.Info("dynamic ports: " + failure);
        return (null, failure);
    }

    /// <summary>Where the last service start noted whether no source gave data (run\dynamic-ports.json).</summary>
    public string? HistoryFile { get; init; }

    /// <summary>This start is the first since Windows started (<see cref="BootMarker"/>): the sources are slow after
    /// every boot, so a failure then says nothing about the system.</summary>
    public bool OsBoot { get; init; }

    private const string NoneAnsweredKey = "none_answered";
    private readonly object _historyLock = new();

    // WARN only when there was nothing to use (the Windows default assumed), the same happened at the previous start,
    // and this start is not the one after a boot (Win10: netsh misses its 3 s at every cold start, VM 2026-09-24)
    private void ReportOutcome(bool noneAnswered, List<string> failures)
    {
        bool before = Remember(NoneAnsweredKey, noneAnswered);
        if (!noneAnswered)
            return;
        string line = $"dynamic ports: no source answered ({string.Join("; ", failures)}), the Windows default is assumed";
        if (before && !OsBoot)
            _log?.Warn(line + "; also at the previous service start");
        else
            _log?.Info(line);
    }

    /// <summary>Stores this start's value of a key; the value of the previous start.</summary>
    private bool Remember(string name, bool failed)
    {
        if (HistoryFile is null)
            return false;
        lock (_historyLock)
        {
            Dictionary<string, bool> history;
            try
            {
                history = File.Exists(HistoryFile)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(HistoryFile)) ?? []
                    : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                history = [];
            }
            bool before = history.GetValueOrDefault(name);
            if (before == failed && File.Exists(HistoryFile))
                return before;
            history[name] = failed;
            try
            {
                File.WriteAllText(HistoryFile, System.Text.Json.JsonSerializer.Serialize(history));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // without the history every failure stays Info
            }
            return before;
        }
    }
}
