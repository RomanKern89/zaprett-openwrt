using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Zaprett.Core;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Tests;

internal sealed class MemoryLog : ILog
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public void Info(string message) => Lines.Enqueue("INFO " + message);
    public void Warn(string message) => Lines.Enqueue("WARN " + message);
    public void Error(string message) => Lines.Enqueue("ERROR " + message);
    public IReadOnlyList<string> Tail(int lines) => Lines.TakeLast(lines).ToList();
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zaprett-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Dispatcher for server tests: records calls, "status"/"version" are read-only.</summary>
internal sealed class FakeDispatcher : ICommandDispatcher
{
    public ConcurrentQueue<(string Method, JsonObject? Args, CallerInfo Caller)> Calls { get; } = new();
    public Func<string, JsonObject?, Task<JsonObject>>? Handler { get; set; }
    public IReadOnlySet<string> ReadOnlyMethods { get; } = new HashSet<string> { "status", "version", "log" };
    public event Action<string, JsonObject>? Event;

    public void Raise(string type, JsonObject data) => Event?.Invoke(type, data);

    public async Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct)
    {
        Calls.Enqueue((method, args, caller));
        if (Handler is not null)
            return await Handler(method, args);
        return new JsonObject { ["ok"] = true, ["method"] = method, ["echo"] = args?.DeepClone() };
    }
}

/// <summary>Process runner that records argv and answers from a script.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];
    public Func<string, IReadOnlyList<string>, ProcessResult> Answer { get; set; } = (_, _) => new ProcessResult(0, "", "", false);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls)
            Calls.Add((fileName, args));
        return Task.FromResult(Answer(fileName, args));
    }
}

internal static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
