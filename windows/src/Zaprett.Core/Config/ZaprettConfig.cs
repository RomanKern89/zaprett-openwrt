namespace Zaprett.Core.Config;

/// <summary>Network filter of the engine (ARCHITECTURE-WIN §5): mode "all" or "ssids" (--ssid-filter), and
/// skip_corporate (--nlm-filter over the networks that are not domain-authenticated).</summary>
public sealed record NetworkFilterConfig(string Mode, IReadOnlyList<string> Ssids, bool SkipCorporate);

public sealed record RepoConfig(string Url, bool Autoupdate, int AutoupdateHour);

public sealed record TestConfig(int Timeout, int Concurrency, int MaxDomains, int Settle);

public sealed record MonitorConfig(bool Present, bool Enabled, int Interval, int Threshold, bool AutoRepair, int MaxTargets, int Timeout);

public sealed record DnsConfig(string Mode);

public sealed record UpdateConfig(string Channel, bool Check);

/// <summary>A URL subscription (router contract v1.1 §4), typed and validated.</summary>
public sealed record SourceConfig(
    string Name, bool Enabled, string Title, string Type, string Url, int IntervalHours, int MinEntries,
    double MinValidRatio, int RamMib, bool Valid, IReadOnlyList<string> BadOptions);

/// <summary>Typed configuration after normalization: every value is valid; invalid ones were replaced by defaults and
/// listed in <see cref="BadOptions"/> (warning bad_config, like config.uc).</summary>
public sealed record ZaprettConfig
{
    /// <summary>The bypass works now (the watchdog and the monitor follow it).</summary>
    public bool Enabled { get; init; }

    /// <summary>The bypass is switched on when Windows starts (the service sets enabled from it at startup).</summary>
    public bool Autostart { get; init; }

    public string Engine { get; init; } = Engines.Winws;
    public string Strategy { get; init; } = "";
    public string StrategyWinws2 { get; init; } = "";
    public string ListMode { get; init; } = "whitelist";
    public IReadOnlyList<string> Lists { get; init; } = [];
    public IReadOnlyList<string> ExcludeLists { get; init; } = [];
    public IReadOnlyList<string> Ipsets { get; init; } = [];
    public IReadOnlyList<string> ExcludeIpsets { get; init; } = [];
    public bool Ipv6 { get; init; }
    public bool Debug { get; init; }
    public bool Watchdog { get; init; } = true;
    public bool QuicBlock { get; init; }
    public bool GameFilter { get; init; }
    public string GamePortsTcp { get; init; } = "1024-65535";
    public string GamePortsUdp { get; init; } = "1024-65535";
    public NetworkFilterConfig NetworkFilter { get; init; } = new("all", [], false);
    public IReadOnlyList<string> DeletedSources { get; init; } = [];
    public RepoConfig Repo { get; init; } = new(ConfigDefaults.RepoUrl, true, 4);
    public TestConfig Test { get; init; } = new(5, 6, 20, 2);
    public MonitorConfig Monitor { get; init; } = new(true, true, 30, 3, false, 5, 8);
    public DnsConfig Dns { get; init; } = new("system");
    public UpdateConfig Update { get; init; } = new("stable", true);
    /// <summary>Language of messages: ru | en | zh-CN (ui.language, ARCHITECTURE-WIN §12.2).</summary>
    public string Language { get; init; } = "ru";
    /// <summary>Unix time when main.debug switches itself off (0 = not set yet).</summary>
    public long DebugUntil { get; init; }
    public IReadOnlyList<SourceConfig> Sources { get; init; } = [];
    public IReadOnlyList<string> BadOptions { get; init; } = [];

    public IReadOnlyList<string> Warnings => BadOptions.Count > 0 ? ["bad_config"] : [];

    /// <summary>The strategy chosen for an engine (main.strategy or main.strategy_winws2).</summary>
    public string CurrentStrategy(string? engine = null) =>
        (engine ?? Engine) == Engines.Winws2 ? StrategyWinws2 : Strategy;

    /// <summary>Active ids of a list option: lists, exclude_lists, ipsets, exclude_ipsets.</summary>
    public IReadOnlyList<string> ListOption(string option) => option switch
    {
        "lists" => Lists,
        "exclude_lists" => ExcludeLists,
        "ipsets" => Ipsets,
        "exclude_ipsets" => ExcludeIpsets,
        _ => [],
    };
}

/// <summary>Engine names on Windows and the router item types their strategies use.</summary>
public static class Engines
{
    public const string Winws = "winws";
    public const string Winws2 = "winws2";

    public static bool IsValid(string? e) => e == Winws || e == Winws2;

    /// <summary>Item type (repository type and bundle directory) of the strategies of an engine.</summary>
    public static string ItemType(string engine) => engine == Winws2 ? "nfqws2" : "nfqws";

    public static string StrategyOption(string engine) => engine == Winws2 ? "strategy_winws2" : "strategy";

    public static string Executable(Platform.IPaths paths, string engine) =>
        engine == Winws2 ? Path.Combine(paths.Engine2Dir, "winws2.exe") : Path.Combine(paths.EngineDir, "winws.exe");
}
