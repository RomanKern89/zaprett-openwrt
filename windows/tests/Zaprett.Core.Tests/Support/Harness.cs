using System.Text.Json.Nodes;

namespace Zaprett.Core.Tests.Support;

/// <summary>A dispatcher over a fake platform.</summary>
public sealed class Harness : IDisposable
{
    public Harness(CoreOptions? options = null, bool engines = true)
    {
        F = new FakePlatform(engines);
        D = new CommandDispatcher(F.Services, options);
        D.Event += (t, d) =>
        {
            lock (Events)
                Events.Add((t, d.DeepClone().AsObject()));
        };
    }

    public FakePlatform F { get; }
    public CommandDispatcher D { get; }
    public List<(string Type, JsonObject Data)> Events { get; } = [];

    public static readonly CallerInfo User = new("user", false, false);

    public Task<JsonObject> Call(string method, JsonObject? args = null, CallerInfo? caller = null) =>
        D.InvokeAsync(method, args, caller ?? CallerInfo.System, CancellationToken.None);

    /// <summary>Starts a job method and waits for it; returns the finished job object.</summary>
    public async Task<JsonObject> Job(string method, JsonObject? args = null)
    {
        var r = await Call(method, args);
        Assert.True(Util.R.IsOk(r), r.ToJsonString());
        await D.Context.Jobs.WhenIdleAsync();
        return D.Context.Jobs.Read()!;
    }

    /// <summary>A background extra monitor check must not outlive the sandbox it writes into (class ZERR-048).</summary>
    public void Dispose()
    {
        var done = true;
        try
        {
            done = D.LastMonitorRecheck?.Wait(TimeSpan.FromSeconds(30)) ?? true;
        }
        catch (AggregateException)
        {
        }
        if (!done)
            throw new TimeoutException("the extra monitor check still runs after 30 s: the sandbox is kept");
        F.Dispose();
    }
}
