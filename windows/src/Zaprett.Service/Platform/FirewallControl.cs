using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// <see cref="IFirewallControl"/>: the Windows Firewall rule "zaprett QUIC block" (outbound UDP 443, all profiles)
/// through netsh advfirewall. Idempotent: the rule is added only when missing and every copy is deleted on disable.
/// </summary>
public sealed class FirewallControl(IProcessRunner runner, ILog log, string? netshPath = null) : IFirewallControl
{
    public const string RuleName = "zaprett QUIC block";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _netsh = netshPath ?? Path.Combine(Environment.SystemDirectory, "netsh.exe");

    public static IReadOnlyList<string> ShowArgs() => ["advfirewall", "firewall", "show", "rule", "name=" + RuleName];

    public static IReadOnlyList<string> AddArgs() =>
    [
        "advfirewall", "firewall", "add", "rule", "name=" + RuleName, "dir=out", "action=block", "protocol=UDP",
        "remoteport=443", "profile=any", "enable=yes", "description=zaprett: block QUIC (UDP 443) so browsers use TCP",
    ];

    public static IReadOnlyList<string> DeleteArgs() => ["advfirewall", "firewall", "delete", "rule", "name=" + RuleName];

    public async Task<bool> IsQuicBlockedAsync(CancellationToken ct)
    {
        // netsh exits 0 when a rule with this name exists and 1 with "No rules match" (text is localized)
        var r = await runner.RunAsync(_netsh, ShowArgs(), Timeout, ct).ConfigureAwait(false);
        if (r.TimedOut)
            throw new TimeoutException("netsh did not answer");
        return r.ExitCode == 0;
    }

    public async Task SetQuicBlockAsync(bool enabled, CancellationToken ct)
    {
        bool present = await IsQuicBlockedAsync(ct).ConfigureAwait(false);
        if (enabled == present)
            return;
        var r = await runner.RunAsync(_netsh, enabled ? AddArgs() : DeleteArgs(), Timeout, ct).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0)
            throw new InvalidOperationException($"netsh {(enabled ? "add" : "delete")} rule failed ({r.ExitCode}): {(r.StdOut + r.StdErr).Trim()}");
        log.Info($"firewall: rule \"{RuleName}\" {(enabled ? "added" : "deleted")}");
    }
}
