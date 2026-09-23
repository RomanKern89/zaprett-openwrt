using Zaprett.Core.Platform;

namespace Zaprett.Core;

/// <summary>Settings of the core given by the service (not by the user).</summary>
public sealed record CoreOptions
{
    /// <summary>Version reported by "version" and "status".</summary>
    public string Version { get; init; } = "0.1.0";

    /// <summary>Automatic selection without stopping the bypass (router contract v1.6 §17, ARCHITECTURE-WIN §7): only when
    /// the service can isolate the check traffic (instance "test" sees only the local ports below, the main instance
    /// excludes them). false → mode exclusive with mode_reason "not_supported".</summary>
    public bool IsolationSupported { get; init; }

    /// <summary>Local port range of the check connections in mode isolated (ProbeRequest.LocalPortFrom/To).</summary>
    public int TestLocalPortFrom { get; init; } = 40000;

    public int TestLocalPortTo { get; init; } = 40100;

    /// <summary>Engine debugging (main.debug) switches itself off after this many minutes: it slows the engine down
    /// 13-20 times (ARCHITECTURE-WIN S-3).</summary>
    public int DebugMinutes { get; init; } = 30;

    /// <summary>How long a started engine must stay up to count as running.</summary>
    public TimeSpan EngineCheckDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>NLM networks for --nlm-filter (network_filter.skip_corporate); null → the filter is not added.</summary>
    public INetworkList? NetworkList { get; init; }

    /// <summary>Program updates (update.check / update.install); null → {ok:false, error:"not_supported"}.</summary>
    public IUpdateControl? Updates { get; init; }
}
