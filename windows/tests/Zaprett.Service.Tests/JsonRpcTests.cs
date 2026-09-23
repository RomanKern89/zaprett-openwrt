using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Ipc;

namespace Zaprett.Service.Tests;

public class JsonRpcTests
{
    private static RpcRequest Parse(string json) => JsonRpc.ParseRequest(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ValidRequest_IsParsed()
    {
        var r = Parse("""{"jsonrpc":"2.0","id":7,"method":"list.enable","params":{"id":"zaprett-youtube"}}""");
        Assert.Equal("list.enable", r.Method);
        Assert.Equal(7, r.Id.GetValue<long>());
        Assert.Equal("zaprett-youtube", r.Params!["id"]!.GetValue<string>());
    }

    [Fact]
    public void StringId_AndNoParams_AreAccepted()
    {
        var r = Parse("""{"jsonrpc":"2.0","id":"abc","method":"status"}""");
        Assert.Equal("abc", r.Id.GetValue<string>());
        Assert.Null(r.Params);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","method":"status"}""", JsonRpc.InvalidRequest)]            // no id
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"status"}""", JsonRpc.InvalidRequest)]     // object id
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"status"}""", JsonRpc.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""", JsonRpc.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"Status"}""", JsonRpc.MethodNotFound)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"../etc"}""", JsonRpc.MethodNotFound)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":""}""", JsonRpc.MethodNotFound)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":[1]}""", JsonRpc.InvalidParams)]
    [InlineData("""[1,2]""", JsonRpc.InvalidRequest)]
    [InlineData("""not json""", JsonRpc.ParseError)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status" """, JsonRpc.ParseError)]
    public void BadRequests_GiveTheRightCode(string json, int code)
    {
        var e = Assert.Throws<JsonRpcException>(() => Parse(json));
        Assert.Equal(code, e.Code);
    }

    [Fact]
    public void InvalidMethod_KeepsIdForTheErrorAnswer()
    {
        var e = Assert.Throws<JsonRpcException>(() => Parse("""{"jsonrpc":"2.0","id":42,"method":"BAD"}"""));
        Assert.Equal(42, e.Id!.GetValue<long>());
    }

    [Fact]
    public void DeepNesting_IsRejected()
    {
        string deep = new string('[', 200) + new string(']', 200);
        var e = Assert.Throws<JsonRpcException>(() => Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"params\":{\"a\":" + deep + "}}"));
        Assert.Equal(JsonRpc.ParseError, e.Code);
    }

    [Fact]
    public void Incoming_ResultErrorAndNotification()
    {
        var res = JsonRpc.ParseIncoming(JsonRpc.Serialize(JsonRpc.Result(JsonValue.Create(3), new JsonObject { ["ok"] = true })));
        Assert.Equal(3, res.Id!.GetValue<long>());
        Assert.True(res.Result!["ok"]!.GetValue<bool>());

        var err = JsonRpc.ParseIncoming(JsonRpc.Serialize(JsonRpc.Error(JsonValue.Create(4), JsonRpc.InternalError, "boom")));
        Assert.Equal(JsonRpc.InternalError, err.ErrorCode);
        Assert.Equal("boom", err.ErrorMessage);

        var note = JsonRpc.ParseIncoming(JsonRpc.Serialize(JsonRpc.Notification("event",
            new JsonObject { ["type"] = "job", ["data"] = new JsonObject { ["x"] = 1 } })));
        Assert.True(note.IsNotification);
        Assert.Equal("job", note.NotificationParams!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Request_RoundTrips()
    {
        var bytes = JsonRpc.Serialize(JsonRpc.Request(9, "wizard.apply", new JsonObject { ["services"] = new JsonArray("youtube", "discord:full") }));
        var r = JsonRpc.ParseRequest(bytes);
        Assert.Equal("wizard.apply", r.Method);
        Assert.Equal("discord:full", r.Params!["services"]![1]!.GetValue<string>());
    }
}
