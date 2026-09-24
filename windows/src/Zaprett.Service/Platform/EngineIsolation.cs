using System.Globalization;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// Isolation of the automatic selection (ARCHITECTURE-WIN §12 S-5, SPIKE-M1 §5): while instance "test" runs, both
/// instances get <c>--wf-raw=@file</c> built from the WinDivert filter of their own strategy (<c>--wf-save … --dry-run</c>)
/// plus a condition on the local TCP ports of the check connections: the candidate sees only that range, the main
/// instance everything else. WinDivert refuses a negated group <c>!(…)</c>, so only De Morgan forms are used.
/// </summary>
public sealed class EngineIsolation(IProcessRunner runner, IPaths paths, ILog log, int portFrom = EngineIsolation.DefaultPortFrom,
    int portTo = EngineIsolation.DefaultPortTo, IDynamicPortRanges? dynamicPorts = null, TimeSpan? portsTimeout = null)
{
    /// <summary>The engine's first start waits for this decision: whatever the ranges source does, not longer than
    /// this (then the Windows default is assumed, as for unreadable ranges).</summary>
    public static readonly TimeSpan DefaultPortsTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _portsTimeout = portsTimeout ?? DefaultPortsTimeout;

    private readonly object _decisionLock = new();
    private Task<bool>? _decision;

    public const int DefaultPortFrom = 40000;
    public const int DefaultPortTo = 40100;
    /// <summary>WinDivert filter length limit.</summary>
    public const int MaxFilterChars = 16 * 1024;
    private static readonly TimeSpan DryRunTimeout = TimeSpan.FromSeconds(30);

    public int PortFrom => portFrom;
    public int PortTo => portTo;

    /// <summary>
    /// May main exclude the check ports for good (so a selection never restarts it)? Only when no dynamic TCP port
    /// range of Windows (IPv4, IPv6) reaches into them: otherwise ordinary connections could get such a local port and
    /// pass main without the bypass. Decided once per service start, the reason is logged. Without a way to read the
    /// ranges, or when they cannot be read, the Windows default (49152-65535, no overlap) is assumed and logged.
    /// </summary>
    public Task<bool> PermanentMainIsolationAsync(CancellationToken ct)
    {
        _ = ct;
        lock (_decisionLock)
            return _decision ??= DecideAsync();
    }

    private async Task<bool> DecideAsync()
    {
        // decided once for the service run, not tied to the first caller's cancellation
        var ct = CancellationToken.None;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<PortRange>? ranges = null;
        if (dynamicPorts is not null)
        {
            try
            {
                ranges = await dynamicPorts.GetTcpAsync(ct).WaitAsync(_portsTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                log.Warn($"engine isolation: the dynamic port ranges were not read within {_portsTimeout.TotalSeconds:0.#} s");
            }
            catch (Exception e)
            {
                // the decision is cached for the whole run: a failed task here would fail every later start of main
                // and test, so any failure means "unknown" (the Windows default)
                log.Warn($"engine isolation: cannot read the dynamic port ranges: {e.GetType().Name}: {e.Message}");
            }
        }
        bool permanent;
        if (ranges is null)
        {
            // the Windows default dynamic range 49152-65535 does not overlap
            permanent = true;
            // the safe default, the normal way when the sources are slow after a boot (they warn themselves when it repeats)
            log.Info($"engine isolation: dynamic TCP port ranges unknown, assuming the Windows default 49152-65535; " +
                     $"main always excludes local TCP ports {portFrom}-{portTo}");
        }
        else if (ranges.FirstOrDefault(r => r.Overlaps(portFrom, portTo)) is { } overlap)
        {
            permanent = false;
            log.Warn($"engine isolation: dynamic TCP port range {overlap} overlaps the check ports {portFrom}-{portTo}, " +
                     "main excludes them only during a test");
        }
        else
        {
            permanent = true;
            log.Info($"engine isolation: main always excludes local TCP ports {portFrom}-{portTo} " +
                     $"(dynamic TCP ports: {string.Join(", ", ranges)})");
        }
        log.Info($"engine isolation: dynamic TCP port ranges read in {sw.Elapsed.TotalSeconds:0.0} s");
        return permanent;
    }

    public static string MainFilter(string saved, int lo, int hi) => string.Format(CultureInfo.InvariantCulture,
        "({0}) and (!tcp or (outbound and (tcp.SrcPort < {1} or tcp.SrcPort > {2})) or (inbound and (tcp.DstPort < {1} or tcp.DstPort > {2})))",
        saved, lo, hi);

    public static string CandidateFilter(string saved, int lo, int hi) => string.Format(CultureInfo.InvariantCulture,
        "({0}) and tcp and ((outbound and tcp.SrcPort >= {1} and tcp.SrcPort <= {2}) or (inbound and tcp.DstPort >= {1} and tcp.DstPort <= {2}))",
        saved, lo, hi);

    /// <summary>The arguments without every <c>--wf-*</c> option (they only build the WinDivert filter, which
    /// <c>--wf-raw</c> replaces).</summary>
    public static List<string> WithoutFilterOptions(IEnumerable<string> args) =>
        args.Where(a => !a.StartsWith("--wf-", StringComparison.Ordinal)).ToList();

    /// <summary>Arguments of an isolated instance: its own filter + the range condition in run\filter-&lt;instance&gt;.txt.</summary>
    public async Task<IReadOnlyList<string>> IsolateAsync(string instance, bool candidate, string executable, IReadOnlyList<string> args,
        CancellationToken ct)
    {
        Directory.CreateDirectory(paths.RunDir);
        string saveFile = Path.Combine(paths.RunDir, $"wfsave-{instance}.txt");
        string filterFile = Path.Combine(paths.RunDir, $"filter-{instance}.txt");
        File.Delete(saveFile);
        var dry = args.Where(a => !a.StartsWith("--wf-save", StringComparison.Ordinal)).ToList();
        dry.Add("--wf-save=" + saveFile);
        dry.Add("--dry-run");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await runner.RunAsync(executable, dry, DryRunTimeout, ct).ConfigureAwait(false);
        var took = sw.Elapsed;
        if (r.TimedOut || r.ExitCode != 0 || !File.Exists(saveFile))
            throw new InvalidOperationException($"--wf-save --dry-run failed ({r.ExitCode}): {(r.StdOut + r.StdErr).Trim()}");
        string saved = (await File.ReadAllTextAsync(saveFile, ct).ConfigureAwait(false)).Trim();
        if (saved.Length == 0)
            throw new InvalidOperationException("--wf-save wrote an empty filter");
        string filter = candidate ? CandidateFilter(saved, portFrom, portTo) : MainFilter(saved, portFrom, portTo);
        if (filter.Length > MaxFilterChars)
            throw new InvalidOperationException($"isolation filter is {filter.Length} characters, WinDivert allows {MaxFilterChars}");
        await File.WriteAllTextAsync(filterFile, filter, ct).ConfigureAwait(false);
        log.Info($"engine {instance}: isolation filter {filterFile} ({filter.Length} chars, {(candidate ? "only" : "without")} local TCP ports {portFrom}-{portTo}; --wf-save {took.TotalSeconds:0.0} s)");
        var result = WithoutFilterOptions(args);
        result.Add("--wf-raw=@" + filterFile);
        return result;
    }
}
