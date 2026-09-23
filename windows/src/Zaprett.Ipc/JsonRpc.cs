using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zaprett.Ipc;

/// <summary>A JSON-RPC 2.0 protocol error (bad JSON, not a request, bad method name, bad params).</summary>
public sealed class JsonRpcException(int code, string message, JsonNode? id = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? Id { get; } = id;
}

public sealed record RpcRequest(JsonNode Id, string Method, JsonObject? Params);

/// <summary>Response or notification received by a client.</summary>
public sealed record RpcIncoming(JsonNode? Id, JsonObject? Result, int? ErrorCode, string? ErrorMessage,
    string? NotificationMethod, JsonObject? NotificationParams)
{
    public bool IsNotification => NotificationMethod is not null;
}

/// <summary>JSON-RPC 2.0 messages of the zaprett pipe (ARCHITECTURE-WIN §6).</summary>
public static partial class JsonRpc
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const string EventMethod = "event";
    public const string SubscribeMethod = "subscribe";

    private static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = 64 };

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodNameRegex();

    public static bool IsValidMethodName(string? name) => name is not null && MethodNameRegex().IsMatch(name);

    public static JsonObject Request(long id, string method, JsonObject? parameters)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            o["params"] = parameters;
        return o;
    }

    public static JsonObject Result(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public static JsonObject Notification(string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };

    public static byte[] Serialize(JsonNode message) => Encoding.UTF8.GetBytes(message.ToJsonString());

    private static JsonObject ParseObject(ReadOnlySpan<byte> utf8)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8, documentOptions: DocOptions);
        }
        catch (JsonException e)
        {
            throw new JsonRpcException(ParseError, "parse error: " + e.Message);
        }
        return node as JsonObject ?? throw new JsonRpcException(InvalidRequest, "message is not a JSON object");
    }

    /// <summary>Server side: parses a request. Throws <see cref="JsonRpcException"/> with the code to answer.</summary>
    public static RpcRequest ParseRequest(ReadOnlySpan<byte> utf8)
    {
        var o = ParseObject(utf8);
        JsonNode? id = o["id"];
        if (id is not JsonValue idv || !(idv.TryGetValue<long>(out _) || idv.TryGetValue<string>(out _)))
            throw new JsonRpcException(InvalidRequest, "request id must be a number or a string");
        if (o["jsonrpc"] is not JsonValue ver || !ver.TryGetValue<string>(out var vs) || vs != "2.0")
            throw new JsonRpcException(InvalidRequest, "jsonrpc must be \"2.0\"", id);
        if (o["method"] is not JsonValue mv || !mv.TryGetValue<string>(out var method))
            throw new JsonRpcException(InvalidRequest, "method must be a string", id);
        if (!IsValidMethodName(method))
            throw new JsonRpcException(MethodNotFound, "invalid method name", id);
        JsonObject? parameters = null;
        if (o["params"] is { } p)
        {
            parameters = p as JsonObject ?? throw new JsonRpcException(InvalidParams, "params must be an object", id);
            o.Remove("params");
        }
        o.Remove("id");
        return new RpcRequest(id, method, parameters);
    }

    /// <summary>Client side: parses a response or an event notification.</summary>
    public static RpcIncoming ParseIncoming(ReadOnlySpan<byte> utf8)
    {
        var o = ParseObject(utf8);
        if (o["method"] is JsonValue mv && mv.TryGetValue<string>(out var method))
        {
            var prm = o["params"] as JsonObject;
            o.Remove("params");
            return new RpcIncoming(null, null, null, null, method, prm ?? []);
        }
        JsonNode? id = o["id"]?.DeepClone();
        if (o["result"] is JsonObject result)
        {
            o.Remove("result");
            return new RpcIncoming(id, result, null, null, null, null);
        }
        if (o["error"] is JsonObject err)
        {
            int code = err["code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : InternalError;
            string msg = err["message"] is JsonValue ev && ev.TryGetValue<string>(out var m) ? m : "error";
            return new RpcIncoming(id, null, code, msg, null, null);
        }
        throw new JsonRpcException(InvalidRequest, "response has neither result nor error");
    }
}
