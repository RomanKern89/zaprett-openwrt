using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

public sealed class StrategyTokenizerTests
{
    [Theory]
    [InlineData("--filter-tcp=443 ${hostlists} --dpi-desync=fake,multidisorder --new")]
    [InlineData("  --dpi-desync-fake-quic=${bin:quic_initial_www_google_com}   --comment=x y")]
    [InlineData("# comment line --new")]
    [InlineData("")]
    [InlineData("--hostlist=${hostlists")]
    public void Tokens_join_back_to_the_line(string line) =>
        Assert.Equal(line, string.Concat(StrategyTokenizer.Tokenize(line).Select(t => t.Text)));

    [Fact]
    public void Kinds_are_recognised()
    {
        var tokens = StrategyTokenizer.Tokenize("--filter-udp=443 ${ipsets} --dpi-desync=fake --dpi-desync-fake-quic=${bin:q} --new");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Filter && t.Text == "--filter-udp=");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Value && t.Text == "443");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Placeholder && t.Text == "${ipsets}");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Option && t.Text == "--dpi-desync=");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Placeholder && t.Text == "${bin:q}");
        Assert.Equal(TokenKind.Separator, tokens[^1].Kind);
    }

    [Fact]
    public void Comment_lines_and_comment_option()
    {
        Assert.All(StrategyTokenizer.Tokenize("# --dpi-desync=fake"), t => Assert.Equal(TokenKind.Comment, t.Kind));
        var tokens = StrategyTokenizer.Tokenize("--comment=note");
        Assert.All(tokens, t => Assert.Equal(TokenKind.Comment, t.Kind));
    }

    [Fact]
    public void Negative_control_plain_word_is_not_an_option()
    {
        var tokens = StrategyTokenizer.Tokenize("filter-tcp=443");
        Assert.DoesNotContain(tokens, t => t.Kind is TokenKind.Option or TokenKind.Filter);
    }
}
