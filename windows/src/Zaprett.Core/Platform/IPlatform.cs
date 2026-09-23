using System.Text.Json.Nodes;

namespace Zaprett.Core.Platform;

// Contract between the platform-independent core (Zaprett.Core) and the Windows service (Zaprett.Service).
// The core never calls Windows APIs, starts processes or opens sockets directly: it goes through these
// interfaces, so every piece of logic can be unit-tested with fakes. See windows/docs/ARCHITECTURE-WIN.md §13.

/// <summary>Filesystem locations (ARCHITECTURE-WIN §3).</summary>
public interface IPaths
{
    string InstallDir { get; }      // C:\Program Files\zaprett
    string BundleDir { get; }       // <InstallDir>\bundle
    string PresetsFile { get; }     // <InstallDir>\presets.json
    string EngineDir { get; }       // <InstallDir>\engine
    string Engine2Dir { get; }      // <InstallDir>\engine2
    string DataDir { get; }         // C:\ProgramData\zaprett
    string ConfigFile { get; }      // <DataDir>\config.json
    string UserDir { get; }         // <DataDir>\user
    string InstalledDir { get; }    // <DataDir>\installed
    string RunDir { get; }          // <DataDir>\run
    string LogDir { get; }          // <DataDir>\logs
}

public interface IClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
}

/// <summary>Result of running an external program.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface IProcessRunner
{
    /// <summary>Runs a program with an argv array (no shell), waits for exit or timeout.</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

/// <summary>State of one engine instance ("main" or "test").</summary>
public sealed record EngineState(string Instance, bool Running, int? Pid, DateTimeOffset? StartedAt, int RestartsInWindow);

/// <summary>Engine lifecycle, implemented by the service (Job Object, restart policy).</summary>
public interface IEngineControl
{
    /// <summary>Starts or restarts an instance with the given executable and argv. Returns when the process is up
    /// (or failed to start within the timeout).</summary>
    Task<EngineState> StartAsync(string instance, string executable, IReadOnlyList<string> args, CancellationToken ct);
    Task StopAsync(string instance, CancellationToken ct);
    EngineState GetState(string instance);
}

/// <summary>One HTTP(S) availability check (router tester semantics, ARCHITECTURE §10.4, §14.3).</summary>
public sealed record ProbeRequest(string Url, long MinBytes, TimeSpan Timeout, int? LocalPortFrom = null, int? LocalPortTo = null);

/// <summary>Error codes are the router's closed list: timeout, reset, tls_cert, tls_error, connect_failed,
/// http_error, too_small, local_error, failed; null when Ok.</summary>
public sealed record ProbeResult(string Url, bool Ok, long Ms, long Bytes, string? Error, int? HttpStatus);

public interface IHttpProbe
{
    Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken ct);
    /// <summary>Downloads a file with a size limit (repository, subscriptions). Throws on error.</summary>
    Task<byte[]> DownloadAsync(string url, long maxBytes, TimeSpan timeout, CancellationToken ct);
}

/// <summary>DNS resolution for diagnosis: system resolver and DNS-over-HTTPS.</summary>
public interface IDnsResolver
{
    Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct);
    Task<IReadOnlyList<string>> ResolveDohAsync(string host, CancellationToken ct);
    /// <summary>TCP connect to ip:port within the timeout (true = connected).</summary>
    Task<bool> TcpConnectAsync(string ip, int port, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Encrypted DNS state and setup (ARCHITECTURE-WIN §5 dns.mode).</summary>
public interface IDnsControl
{
    Task<JsonObject> GetStatusAsync(CancellationToken ct);           // {encrypted, provider, mode}
    Task<JsonObject> SetupAsync(bool enable, CancellationToken ct);  // {ok, changed, ...}
}

/// <summary>Firewall rule for QUIC blocking (UDP 443 outbound), owned and removed by zaprett.</summary>
public interface IFirewallControl
{
    Task SetQuicBlockAsync(bool enabled, CancellationToken ct);
    Task<bool> IsQuicBlockedAsync(CancellationToken ct);
}

/// <summary>Conflicting software scan (ARCHITECTURE-WIN §8).</summary>
public interface IConflictScanner
{
    Task<JsonArray> ScanAsync(CancellationToken ct);
}

/// <summary>Platform facts for status (ARCHITECTURE-WIN §6).</summary>
public interface ISystemInfo
{
    Task<JsonObject> GetPlatformAsync(CancellationToken ct);   // {os, build, arch, hvci, smart_app_control, defender}
    Task<JsonObject> GetWinDivertAsync(CancellationToken ct);  // {loaded, version, foreign:[...]}
    long? MemoryAvailableMiB { get; }
}

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    /// <summary>Last lines of the service and engine log (for the "log" method).</summary>
    IReadOnlyList<string> Tail(int lines);
}

/// <summary>Everything the core needs from the platform.</summary>
public sealed record PlatformServices(
    IPaths Paths,
    IClock Clock,
    IProcessRunner Processes,
    IEngineControl Engine,
    IHttpProbe Http,
    IDnsResolver Dns,
    IDnsControl DnsControl,
    IFirewallControl Firewall,
    IConflictScanner Conflicts,
    ISystemInfo System,
    ILog Log);
