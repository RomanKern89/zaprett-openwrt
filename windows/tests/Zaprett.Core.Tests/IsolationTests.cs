using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>Isolated automatic selection against a fake engine that behaves like the service (S-5: "main" is restarted
/// with the exclusion filter while "test" runs). Regression found on the Windows 11 test machine: the core stopped "test" after its setup check,
/// which cost two extra restarts of the main engine and let the main engine desync the baseline checks.</summary>
public sealed class IsolationTests
{
    [Fact]
    public async Task IsolatedTest_RestartsMainOnlyForTheIsolation_AndMeasuresTheBaselineWithoutBypass()
    {
        using var h = new Harness(new CoreOptions { IsolationSupported = true });
        await h.Call("start");
        h.F.Engine.ModelIsolation = true;
        var mainArgs = h.F.Engine.ArgsOf("main")!.ToList();
        // who handled every check: "test" instance args at the moment of the request (null = not running)
        var seen = new ConcurrentQueue<IReadOnlyList<string>?>();
        h.F.Http.Probe = r =>
        {
            seen.Enqueue(h.F.Engine.ArgsOf("test"));
            return new ProbeResult(r.Url, false, 5, 0, "reset", null);
        };
        var j = await h.Job("test.start", new JsonObject { ["strategies"] = new JsonArray("strategy-general", "strategy-alt") });
        Assert.Equal("isolated", R.Str(j["result"]!["mode"]));

        // the main engine: started once by "start", then exactly one restart into the isolation and one back
        var log = h.F.Engine.Log.ToList();
        Assert.Equal(3, log.Count(l => l == "start main"));
        Assert.Equal(2, log.Count(l => l == "stop main"));
        Assert.Equal(mainArgs, h.F.Engine.ArgsOf("main"));
        // "test" is stopped once, at the end
        Assert.Equal(1, log.Count(l => l == "stop test"));
        Assert.Equal("stop test", log[^3]);

        // every check ran while "test" was up; the baseline ones through the pass-through strategy (guard host only)
        var all = seen.ToList();
        Assert.All(all, a => Assert.NotNull(a));
        var targets = all.Count / 3;
        var baseline = all.Take(targets).ToList();
        var guard = Path.Combine(h.F.Paths.DataDir, "guard", "hostlist-guard.txt");
        Assert.All(baseline, a => Assert.Contains("--hostlist=" + guard, a!));
        Assert.All(baseline, a => Assert.DoesNotContain(a!, x => x.StartsWith("--dpi-desync", StringComparison.Ordinal)));
    }
}
