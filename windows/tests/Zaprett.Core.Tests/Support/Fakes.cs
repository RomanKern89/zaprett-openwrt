using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Zaprett.Core.Platform;

namespace Zaprett.Core.Tests.Support;

public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        lock (Delays)
            Delays.Add(delay);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>Answers every program with the configured result; records the calls.</summary>
public sealed class FakeProcesses : IProcessRunner
{
    public ConcurrentQueue<(string File, List<string> Args)> Calls { get; } = new();

    public Func<string, IReadOnlyList<string>, ProcessResult> Handler { get; set; } =
        (f, a) => a.Contains("--version") ? new ProcessResult(0, "winws github version v72.13\n", "", false) : new ProcessResult(0, "", "", false);

    /// <summary>Behaves like winws v72.13 when set: the duplicate check (mutex over the WinDivert filter options) comes
    /// before --dry-run, --wf-save exits before it.</summary>
    public FakeEngine? RunningEngine { get; set; }

    static readonly (string Opt, string Kind)[] ListFileOptions =
        [("--hostlist=", "hostlist"), ("--hostlist-exclude=", "hostlist"), ("--ipset=", "ipset"), ("--ipset-exclude=", "ipset")];

    static readonly string[] FilterOptions = ["--wf-l3=", "--wf-tcp=", "--wf-udp=", "--wf-raw=", "--wf-raw-part=", "--ssid-filter=", "--nlm-filter="];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Enqueue((fileName, args.ToList()));
        var save = args.FirstOrDefault(a => a.StartsWith("--wf-save=", StringComparison.Ordinal))?["--wf-save=".Length..];
        if (RunningEngine != null && save == null && fileName.EndsWith("winws.exe", StringComparison.OrdinalIgnoreCase) &&
            RunningEngine.ArgsOf("main") is { } running &&
            running.Where(a => FilterOptions.Any(a.StartsWith)).SequenceEqual(args.Where(a => FilterOptions.Any(a.StartsWith))))
            return Task.FromResult(new ProcessResult(1, "", "A copy of winws is already running with the same filter\n", false));
        // like winws: a list file that does not exist fails the check ("cannot access hostlist file '...'")
        if (args.Count > 0 && args[0] is "--dry-run" or "--intercept=0")
            foreach (var a in args)
                foreach (var (opt, kind) in ListFileOptions)
                    if (a.StartsWith(opt, StringComparison.Ordinal) && !File.Exists(a[opt.Length..]))
                        return Task.FromResult(new ProcessResult(1, $"cannot access {kind} file '{a[opt.Length..]}'\n", "", false));
        var r = Handler(fileName, args);
        if (r.ExitCode == 0 && !r.TimedOut && save != null)
            File.WriteAllText(save, "outbound and tcp.DstPort == 443\n");
        return Task.FromResult(r);
    }
}

/// <summary>Engine instances in memory. <see cref="FailWhen"/> decides that a start fails (e.g. a bad strategy).</summary>
public sealed class FakeEngine : IEngineControl
{
    readonly ConcurrentDictionary<string, (bool Running, List<string> Args, DateTimeOffset At)> inst = new();
    readonly FakeClock clock;
    int pid = 1000;

    public FakeEngine(FakeClock clock)
    {
        this.clock = clock;
    }

    public Func<string, IReadOnlyList<string>, bool> FailWhen { get; set; } = (_, _) => false;

    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>Phase reported for running instances (winws waiting for the network of a network filter).</summary>
    public string Phase { get; set; } = EnginePhases.Capturing;

    public IReadOnlyList<string>? ArgsOf(string instance) => inst.TryGetValue(instance, out var s) && s.Running ? s.Args : null;

    /// <summary>Behave like the service's EngineControl with isolation (S-5): starting "test" restarts a running "main"
    /// with the exclusion filter, stopping "test" restarts "main" back with its own arguments.</summary>
    public bool ModelIsolation { get; set; }

    bool mainIsolated;

    void RestartMain(bool isolate)
    {
        if (!inst.TryGetValue("main", out var m) || !m.Running || mainIsolated == isolate)
            return;
        Log.Enqueue("stop main");
        inst["main"] = (true, m.Args, clock.Now);
        Log.Enqueue("start main");
        mainIsolated = isolate;
    }

    public Task<EngineState> StartAsync(string instance, string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        var fail = FailWhen(instance, args);
        if (ModelIsolation && instance == "test" && !fail)
            RestartMain(true);
        inst[instance] = (!fail, args.ToList(), clock.Now);
        Log.Enqueue($"start {instance}{(fail ? " failed" : "")}");
        return Task.FromResult(GetState(instance));
    }

    /// <summary>Instances whose stop throws (a broken service).</summary>
    public HashSet<string> ThrowOnStop { get; } = [];

    public Task StopAsync(string instance, CancellationToken ct)
    {
        if (ThrowOnStop.Contains(instance))
            throw new InvalidOperationException("stop failed: " + instance);
        if (inst.TryRemove(instance, out _))
            Log.Enqueue($"stop {instance}");
        if (ModelIsolation && instance == "test")
            RestartMain(false);
        return Task.CompletedTask;
    }

    public EngineState GetState(string instance) =>
        inst.TryGetValue(instance, out var s) && s.Running
            ? new EngineState(instance, true, Interlocked.Increment(ref pid), s.At, 0, Phase)
            : new EngineState(instance, false, null, null, 0);

    /// <summary>Simulates a crash of the engine.</summary>
    public void Kill(string instance) => inst.TryRemove(instance, out _);
}

public sealed class FakeHttp : IHttpProbe
{
    public Func<ProbeRequest, ProbeResult> Probe { get; set; } = r => new ProbeResult(r.Url, true, 50, 200000, null, 200);

    public ConcurrentDictionary<string, Func<byte[]>> Downloads { get; } = new();

    public ConcurrentQueue<ProbeRequest> Requests { get; } = new();

    public Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Task.FromResult(Probe(request));
    }

    public Task<byte[]> DownloadAsync(string url, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        if (!Downloads.TryGetValue(url, out var f))
            throw new HttpRequestException("404", null, System.Net.HttpStatusCode.NotFound);
        var b = f();
        if (b.LongLength > maxBytes)
            throw new Checks.DownloadException("too_large", "limit");
        return Task.FromResult(b);
    }
}

public sealed class FakeDns : IDnsResolver
{
    public Dictionary<string, List<string>> System { get; } = [];
    public Dictionary<string, List<string>?> Doh { get; } = [];
    public Func<string, int, bool> Tcp { get; set; } = (_, _) => true;

    public Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(System.TryGetValue(host, out var l) ? l : []);

    public Task<IReadOnlyList<string>> ResolveDohAsync(string host, CancellationToken ct)
    {
        if (Doh.TryGetValue(host, out var l) && l == null)
            throw new HttpRequestException("doh failed");
        return Task.FromResult<IReadOnlyList<string>>(l ?? []);
    }

    public Task<bool> TcpConnectAsync(string ip, int port, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Tcp(ip, port));
}

public sealed class FakeDnsControl : IDnsControl
{
    public bool Encrypted { get; set; }
    public int Setups { get; private set; }

    public Task<JsonObject> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new JsonObject { ["encrypted"] = Encrypted, ["provider"] = Encrypted ? "windows-doh" : null, ["mode"] = Encrypted ? "doh" : "system" });

    public Task<JsonObject> SetupAsync(bool enable, CancellationToken ct)
    {
        Setups++;
        Encrypted = enable;
        return Task.FromResult(new JsonObject { ["ok"] = true, ["changed"] = true });
    }
}

public sealed class FakeFirewall : IFirewallControl
{
    public bool Blocked { get; set; }
    public int Sets { get; private set; }

    public Task SetQuicBlockAsync(bool enabled, CancellationToken ct)
    {
        Sets++;
        Blocked = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> IsQuicBlockedAsync(CancellationToken ct) => Task.FromResult(Blocked);
}

public sealed class FakeConflicts : IConflictScanner
{
    public static JsonObject Winws() => new()
    {
        ["id"] = "zapret", ["name"] = "zapret / winws (another installation)", ["severity"] = "block", ["detail"] = "process winws.exe is running",
        ["fix"] = "stop it", ["kind"] = "process", ["path"] = @"C:\zt\foreign\winws.exe", ["pid"] = 4242,
    };

    public static JsonObject Vpn() => new() { ["id"] = "vpn:wg", ["name"] = "VPN: wg", ["severity"] = "info", ["detail"] = "vpn", ["fix"] = "-" };

    /// <summary>What the scan finds; nothing by default. Throws when <see cref="Fail"/> is set.</summary>
    public List<JsonObject> Items { get; } = [];

    public bool Fail { get; set; }

    public int Scans { get; private set; }

    public Task<JsonArray> ScanAsync(CancellationToken ct)
    {
        Scans++;
        if (Fail)
            throw new InvalidOperationException("scan failed");
        return Task.FromResult(new JsonArray(Items.Select(i => (JsonNode)i.DeepClone()).ToArray()));
    }
}

public sealed class FakeSystemInfo : ISystemInfo
{
    public long? MemoryAvailableMiB { get; set; } = 8192;

    public Task<JsonObject> GetPlatformAsync(CancellationToken ct) => Task.FromResult(new JsonObject
    {
        ["os"] = "Windows 11", ["build"] = 26100, ["arch"] = "x64", ["hvci"] = false, ["smart_app_control"] = "off", ["defender"] = true,
    });

    public Task<JsonObject> GetWinDivertAsync(CancellationToken ct) =>
        Task.FromResult(new JsonObject { ["loaded"] = true, ["version"] = "2.2.2", ["foreign"] = new JsonArray() });
}

public sealed class FakeLog : ILog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Info(string message) => Lines.Enqueue("I " + message);
    public void Warn(string message) => Lines.Enqueue("W " + message);
    public void Error(string message) => Lines.Enqueue("E " + message);
    public IReadOnlyList<string> Tail(int lines) => Lines.TakeLast(lines).ToList();
}

/// <summary>A whole fake platform over a sandbox layout.</summary>
public sealed class FakePlatform : IDisposable
{
    public FakePlatform(bool engines = true)
    {
        Paths = new TestPaths();
        if (engines)
            Paths.CreateEngines();
        Engine = new FakeEngine(Clock);
        Processes.RunningEngine = Engine;
        Services = new PlatformServices(Paths, Clock, Processes, Engine, Http, Dns, DnsControl, Firewall, Conflicts, System, Log);
    }

    public TestPaths Paths { get; }
    public FakeClock Clock { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public FakeEngine Engine { get; }
    public FakeHttp Http { get; } = new();
    public FakeDns Dns { get; } = new();
    public FakeDnsControl DnsControl { get; } = new();
    public FakeFirewall Firewall { get; } = new();
    public FakeConflicts Conflicts { get; } = new();
    public FakeSystemInfo System { get; } = new();
    public FakeLog Log { get; } = new();
    public PlatformServices Services { get; }

    public void Dispose() => Paths.Dispose();
}
