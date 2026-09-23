using System.Reflection;
using System.Text.Json.Nodes;
using Zaprett.Core;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Hosting;

/// <summary>
/// Stand-in for the core (--stub, tests of IPC and CLI without the core): answers status, version,
/// log, conflicts and dns.status from the platform layer; everything else is {ok:false, error:"not_implemented"}.
/// </summary>
public sealed class StubDispatcher(PlatformServices platform) : ICommandDispatcher
{
    private static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal)
    {
        "status", "version", "log", "conflicts", "dns.status", "items", "presets", "strategy.show", "user.get", "repo.list",
        "sources.list", "test.status", "job.status", "job.log", "probe.status", "monitor.status", "diagnose.status", "diag",
        "page", "settings.get", "update.check",
    };

    public IReadOnlySet<string> ReadOnlyMethods => ReadOnly;

    public event Action<string, JsonObject>? Event;

    public async Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct)
    {
        switch (method)
        {
            case "version":
                return new JsonObject { ["ok"] = true, ["version"] = Version, ["core"] = "stub" };
            case "status":
                var main = platform.Engine.GetState("main");
                return new JsonObject
                {
                    ["ok"] = true,
                    ["stub"] = true,
                    ["running"] = main.Running,
                    ["engine_stats"] = new JsonObject
                    {
                        ["pid"] = main.Pid,
                        ["uptime_s"] = main.StartedAt is { } t ? (long)(platform.Clock.Now - t).TotalSeconds : null,
                    },
                    ["platform"] = await platform.System.GetPlatformAsync(ct).ConfigureAwait(false),
                    ["windivert"] = await platform.System.GetWinDivertAsync(ct).ConfigureAwait(false),
                    ["memory_available_mib"] = platform.System.MemoryAvailableMiB,
                    ["caller"] = new JsonObject { ["user"] = caller.UserName, ["can_modify"] = caller.CanModify },
                };
            case "log":
                int tail = args?["tail"] is JsonValue v && v.TryGetValue<int>(out var n) && n > 0 ? n : 200;
                return new JsonObject { ["ok"] = true, ["lines"] = new JsonArray(platform.Log.Tail(tail).Select(l => (JsonNode)l).ToArray()) };
            case "conflicts":
                return new JsonObject { ["ok"] = true, ["items"] = await platform.Conflicts.ScanAsync(ct).ConfigureAwait(false) };
            case "dns.status":
                var dns = await platform.DnsControl.GetStatusAsync(ct).ConfigureAwait(false);
                dns["ok"] = true;
                return dns;
            case "ensure":
            case "monitor.run":
                Event?.Invoke("monitor", new JsonObject { ["method"] = method, ["stub"] = true });
                return new JsonObject { ["ok"] = true, ["stub"] = true };
            default:
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "not_implemented",
                    ["message"] = $"'{method}' is not available: the service runs without the core (stub dispatcher)",
                };
        }
    }

    private static string Version =>
        typeof(StubDispatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}

/// <summary>The core dispatcher, or the stub with --stub (service without the core, for IPC/CLI checks).</summary>
public static class DispatcherFactory
{
    public static ICommandDispatcher Create(PlatformServices platform, bool forceStub, ILog log, CoreOptions options)
    {
        if (forceStub)
        {
            log.Warn("core: stub dispatcher (--stub)");
            return new StubDispatcher(platform);
        }
        return new CommandDispatcher(platform, options);
    }
}

/// <summary>How the process runs: under the service control manager as SYSTEM, or in a console for development.</summary>
public sealed record ServiceMode(bool IsService);
