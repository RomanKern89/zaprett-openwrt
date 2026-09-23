using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("strategy-general", true)]
    [InlineData("user-my.one_2", true)]
    [InlineData("", false)]
    [InlineData("bad id", false)]
    [InlineData("../etc", false)]
    [InlineData("с-кириллицей", false)]
    public void Ids(string id, bool valid) => Assert.Equal(valid, Validation.IsId(id));

    [Fact]
    public void Id_longer_than_96_is_rejected() => Assert.False(Validation.IsId(new string('a', 97)));

    [Theory]
    [InlineData("user-x", true)]
    [InlineData("user-", false)]
    [InlineData("strategy-general", false)]
    [InlineData("user x", false)]
    public void Own_strategy_ids(string id, bool valid) => Assert.Equal(valid, Validation.IsUserStrategyId(id));

    [Theory]
    [InlineData("example.org", true)]
    [InlineData("sub.example.co.uk", true)]
    [InlineData("*.example.org", true)]
    [InlineData("xn--80ak6aa92e.com", true)]
    [InlineData("localhost", false)]
    [InlineData("пример.рф", false)]
    [InlineData("exa mple.org", false)]
    [InlineData("-bad.org", false)]
    public void Domains(string line, bool valid) => Assert.Equal(valid, Validation.IsDomain(line));

    [Theory]
    [InlineData("https://example.org/list.txt", true)]
    [InlineData("http://example.org/list.txt", false)]
    [InlineData("ftp://example.org", false)]
    [InlineData("not a url", false)]
    public void Subscription_urls(string url, bool valid) => Assert.Equal(valid, Validation.IsHttpsUrl(url));

    [Fact]
    public void Masking_hides_queries_but_keeps_the_rest()
    {
        var text = "src https://host/list.lst?token=SECRET&x=1 and https://plain/path ok";
        var masked = Validation.MaskUrls(text);
        Assert.DoesNotContain("SECRET", masked, StringComparison.Ordinal);
        Assert.Contains("https://host/list.lst?…", masked, StringComparison.Ordinal);
        Assert.Contains("https://plain/path ok", masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("My YouTube", "user-my-youtube")]
    [InlineData("strategy-general", "user-strategy-general")]
    [InlineData("user-x", "user-x")]
    [InlineData("!!!", "user-strategy")]
    public void Own_strategy_id_from_a_name(string name, string id)
    {
        Assert.Equal(id, Validation.ToUserStrategyId(name));
        Assert.True(Validation.IsUserStrategyId(Validation.ToUserStrategyId(name)));
    }

    [Theory]
    [InlineData("1024-65535", true)]
    [InlineData("80,443,50000-50100", true)]
    [InlineData("0", false)]
    [InlineData("70000", false)]
    [InlineData("500-100", false)]
    [InlineData("80,,443", false)]
    public void Game_ports(string text, bool valid) => Assert.Equal(valid, SettingsViewModel.IsPortList(text));

    [Fact]
    public void Subscription_name_from_title()
    {
        Assert.Equal("my_list_2", ListsViewModel.SourceName("My list #2"));
        Assert.Equal("source", ListsViewModel.SourceName("Список"));
        Assert.Equal(32, ListsViewModel.SourceName(new string('a', 50)).Length);
        Assert.True(Validation.IsSourceName(ListsViewModel.SourceName("Re:filter — домены")));
    }

    [Fact]
    public void Json_readers_are_tolerant()
    {
        var o = Make.Json("""{"s":"x","n":5,"f":2.9,"b":true,"bs":"1","ns":"42","arr":["a",1,"b"],"obj":{"k":"v"},"nul":null}""");
        Assert.Equal("x", o.Str("s"));
        Assert.Equal("5", o.Str("n"));
        Assert.Equal(2, o.Long("f"));
        Assert.Equal(42, o.Int("ns"));
        Assert.True(o.Bool("b"));
        Assert.True(o.Bool("bs"));
        Assert.True(o.Bool("n"));
        Assert.Equal(["a", "b"], o.Strings("arr"));
        Assert.Equal("v", o.Obj("obj").Str("k"));
        Assert.Null(o.Str("nul"));
        Assert.Null(o.Obj("s"));
        Assert.Empty(o.Arr("missing"));
        // nodes built in code (not parsed) must read the same as parsed ones
        var built = new JsonObject { ["i"] = 5, ["l"] = 7L, ["d"] = 0.5, ["b"] = 1 };
        Assert.Equal(5, built.Long("i"));
        Assert.Equal(7, built.Int("l"));
        Assert.Equal(0.5, built.Double("d"));
        Assert.True(built.Bool("b"));
        JsonNode? none = null;
        Assert.Equal(7, none.Long("x", 7));
        Assert.False(none.IsOk());
    }
}
