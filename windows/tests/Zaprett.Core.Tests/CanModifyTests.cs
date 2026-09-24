using System.Text.Json.Nodes;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>status.can_modify is the caller's right to change settings, so the app shows locked switches in advance.</summary>
public sealed class CanModifyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task Status_CarriesTheCallersRight(bool admin, bool op, bool expected)
    {
        using var h = new Harness();
        var s = await h.Call("status", null, new CallerInfo("someone", admin, op));
        Assert.True(R.IsOk(s), s.ToJsonString());
        Assert.Equal(JsonValueKind(expected), s["can_modify"]!.GetValueKind());
        // the answer matches what the core really allows this caller
        var set = await h.Call("settings.set", new JsonObject { ["main"] = new JsonObject { ["quic_block"] = true } }, new CallerInfo("someone", admin, op));
        Assert.Equal(expected, R.IsOk(set));
        if (!expected)
            Assert.Equal("access_denied", R.Error(set));
    }

    [Fact]
    public async Task EachCallerGetsItsOwnValue_AlsoInPage()
    {
        using var h = new Harness();
        var user = h.Call("status", null, Harness.User);
        var op = h.Call("status", null, new CallerInfo("op", false, true));
        Assert.False(R.Bool((await user)["can_modify"]));
        Assert.True(R.Bool((await op)["can_modify"]));
        var page = await h.Call("page", new JsonObject { ["name"] = "overview" }, Harness.User);
        Assert.False(R.Bool(page["status"]!["can_modify"]));
    }

    static System.Text.Json.JsonValueKind JsonValueKind(bool b) =>
        b ? System.Text.Json.JsonValueKind.True : System.Text.Json.JsonValueKind.False;
}
