using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Cli;
using Zaprett.Ipc;

namespace Zaprett.Service.Tests;

public class StdinTextTests
{
    private const string Json = "{\"main\":{\"debug\":true},\"note\":\"тест 测试\"}";

    private static byte[] Bom(Encoding e) => e.GetPreamble();

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // every way the same text can arrive on stdin (PowerShell 5.1 $OutputEncoding variants, cmd < file, editors)
    public static IEnumerable<object[]> Variants()
    {
        var utf8 = new UTF8Encoding(false);
        yield return ["utf8", utf8.GetBytes(Json)];
        yield return ["utf8-bom", Cat(Bom(Encoding.UTF8), utf8.GetBytes(Json))];
        yield return ["utf8-bom-crlf", Cat(Bom(Encoding.UTF8), utf8.GetBytes(Json + "\r\n"))];
        yield return ["utf16le-bom", Cat(Bom(Encoding.Unicode), Encoding.Unicode.GetBytes(Json + "\r\n"))];
        yield return ["utf16be-bom", Cat(Bom(Encoding.BigEndianUnicode), Encoding.BigEndianUnicode.GetBytes(Json + "\r\n"))];
        yield return ["utf16le-bom+utf8-bom-char", Cat(Bom(Encoding.Unicode), Encoding.Unicode.GetBytes("\uFEFF" + Json))];
        yield return ["trailing-spaces-nul", Cat(utf8.GetBytes(Json + " \t\r\n\r\n"), new byte[] { 0 })];
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void AllEncodings_GiveTheSameText(string name, byte[] bytes)
    {
        Assert.Equal(Json, StdinText.Decode(bytes));
        Assert.NotNull(JsonNode.Parse(StdinText.Decode(bytes)) as JsonObject);
        Assert.NotEmpty(name);
    }

    [Fact]
    public void AsciiUtf16WithoutBom_IsRecognized()
    {
        const string ascii = "{\"main\":{\"debug\":true}}";
        Assert.Equal(ascii, StdinText.Decode(Encoding.Unicode.GetBytes(ascii + "\r\n")));
    }

    [Fact]
    public void AsciiFromPowerShellDefault_Passes()
    {
        // PowerShell 5.1 default $OutputEncoding is ASCII and it appends CRLF
        Assert.Equal("{\"main\":{\"debug\":true}}", StdinText.Decode(Encoding.ASCII.GetBytes("{\"main\":{\"debug\":true}}\r\n")));
    }

    [Fact]
    public void NotUtf8_FallsBackToTheOemCodePage()
    {
        // cp866 "тест" from a Russian cmd without chcp 65001; on other systems it just must not throw
        byte[] cp866 = [0xE2, 0xA5, 0xE1, 0xE2];
        Assert.Equal(4, StdinText.Decode(cp866).Length);
    }

    [Fact]
    public async Task Limit_IsInBytes()
    {
        Assert.Null(await StdinText.ReadAsync(new MemoryStream(new byte[101]), 100));
        Assert.Equal(100, (await StdinText.ReadAsync(new MemoryStream(Enumerable.Repeat((byte)'x', 100).ToArray()), 100))!.Length);
    }

    private static async Task<(int Rc, string Err, FakeClient Client)> Cli(string[] argv, byte[] stdin)
    {
        var client = new FakeClient((_, _) => new JsonObject { ["ok"] = true });
        var e = new StringWriter();
        int rc = await CliApp.RunAsync([.. argv, "--lang", "en"], () => client, new MemoryStream(stdin), new StringWriter(), e);
        return (rc, e.ToString(), client);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task SettingsSet_AcceptsEveryEncoding(string name, byte[] bytes)
    {
        var (rc, err, client) = await Cli(["settings", "set"], bytes);
        Assert.True(rc == 0, name + ": " + err);
        var args = client.Calls.Single().Args!;
        Assert.True(args["main"]!["debug"]!.GetValue<bool>());
        Assert.Equal("тест 测试", args["note"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task TextCommands_GetTheTextWithoutBom(string name, byte[] bytes)
    {
        foreach (var argv in new[] { new[] { "strategy", "save", "user-x" }, ["user", "set", "user-hosts"] })
        {
            var (rc, err, client) = await Cli(argv, bytes);
            Assert.True(rc == 0, name + ": " + err);
            Assert.Equal(Json, client.Calls.Single().Args!["text"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("not json", "Input must be a JSON object:")]
    [InlineData("[1,2]", "it is not an object")]
    [InlineData("", "the input is empty")]
    [InlineData("\uFEFF\r\n", "the input is empty")]
    public async Task NotJson_IsAClearUsageError(string input, string expected)
    {
        foreach (var argv in new[] { new[] { "settings", "set" }, ["sources", "save", "my_list"] })
        {
            var (rc, err, client) = await Cli(argv, Encoding.UTF8.GetBytes(input));
            Assert.Equal(2, rc);
            Assert.Contains(expected, err);
            Assert.Empty(client.Calls);
        }
    }
}
