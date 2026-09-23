using System.Text.Json.Nodes;

namespace Zaprett.Core;

/// <summary>Who is calling (from the pipe client token), ARCHITECTURE-WIN §6.</summary>
public sealed record CallerInfo(string UserName, bool IsAdmin, bool IsOperator)
{
    public bool CanModify => IsAdmin || IsOperator;
    public static CallerInfo System { get; } = new("SYSTEM", true, true);
}

/// <summary>
/// Entry point of the core: one method per IPC/CLI command (names from ARCHITECTURE-WIN §6, e.g. "status",
/// "wizard.apply"). Every answer is the router-compatible JSON object: {"ok":true,...} or
/// {"ok":false,"error":"code","message":"text"}. Implemented in Zaprett.Core, hosted by Zaprett.Service.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>Methods that only read state (allowed for any interactive user).</summary>
    IReadOnlySet<string> ReadOnlyMethods { get; }

    Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct);

    /// <summary>Background events for "subscribe": type is status|job|probe|monitor.</summary>
    event Action<string, JsonObject>? Event;
}
