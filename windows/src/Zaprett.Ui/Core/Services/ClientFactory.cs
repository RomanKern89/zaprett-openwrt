using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Zaprett.Ipc;
using Zaprett.Ui.Core.DevFakes;

namespace Zaprett.Ui.Core.Services;

/// <summary>
/// Chooses the service client: the fake one for development (--fake[=scenario] or ZAPRETT_UI_FAKE=1), otherwise the
/// named-pipe client found by type name, so the interface builds even before the real client exists.
/// </summary>
public static class ClientFactory
{
    public const string PipeClientType = "Zaprett.Ipc.ZaprettPipeClient, Zaprett.Ipc";

    public static bool IsFake(IReadOnlyList<string> args, Func<string, string?> env) =>
        args.Any(a => a == "--fake" || a.StartsWith("--fake=", StringComparison.Ordinal)) || env("ZAPRETT_UI_FAKE") == "1";

    public static string FakeScenario(IReadOnlyList<string> args, Func<string, string?> env)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--fake=", StringComparison.Ordinal));
        return arg?["--fake=".Length..] ?? env("ZAPRETT_UI_FAKE_SCENARIO") ?? FakeZaprettClient.Scenarios.Running;
    }

    public static IZaprettClient Create(IReadOnlyList<string> args, Func<string, string?> env)
    {
        if (IsFake(args, env))
            return new FakeZaprettClient(FakeScenario(args, env));
        return CreateByTypeName(PipeClientType);
    }

    /// <summary>Instantiates a client type; constructor parameters with default values get those defaults.
    /// A missing type or a failing constructor gives a client that reports "service unavailable".</summary>
    public static IZaprettClient CreateByTypeName(string typeName)
    {
        var type = Type.GetType(typeName, throwOnError: false);
        if (type == null || !typeof(IZaprettClient).IsAssignableFrom(type))
            return new UnavailableClient($"type {typeName} not found");
        try
        {
            var ctor = type.GetConstructors()
                .Where(c => c.GetParameters().All(p => p.HasDefaultValue))
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor == null)
                return new UnavailableClient($"{type.Name} has no usable constructor");
            return (IZaprettClient)ctor.Invoke(ctor.GetParameters().Select(p => p.DefaultValue).ToArray());
        }
        catch (Exception e) when (e is MemberAccessException or System.Reflection.TargetInvocationException or ArgumentException)
        {
            return new UnavailableClient(e.InnerException?.Message ?? e.Message);
        }
    }
}

/// <summary>Stand-in when no client could be created: every call reports the service as unavailable.</summary>
public sealed class UnavailableClient(string reason) : IZaprettClient
{
    public string Reason { get; } = reason;

    public Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default) =>
        Task.FromException<JsonObject>(new ZaprettUnavailableException(Reason));

    public async IAsyncEnumerable<ServiceEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw new ZaprettUnavailableException(Reason);
#pragma warning disable CS0162 // an async iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
