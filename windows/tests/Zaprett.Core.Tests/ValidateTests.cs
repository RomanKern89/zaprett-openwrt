using Zaprett.Core.Validation;

namespace Zaprett.Core.Tests;

public sealed class ValidateTests
{
    [Theory]
    [InlineData("zaprett-youtube", true)]
    [InlineData("a", true)]
    [InlineData("A.b_c-9", true)]
    [InlineData("", false)]
    [InlineData(".hidden", false)]
    [InlineData("bad id", false)]
    [InlineData("../x", false)]
    [InlineData("ü", false)]
    public void IsId(string s, bool ok) => Assert.Equal(ok, Validate.IsId(s));

    [Fact]
    public void IsId_LengthLimit()
    {
        Assert.True(Validate.IsId(new string('a', 96)));
        Assert.False(Validate.IsId(new string('a', 97)));
        Assert.False(Validate.IsId(null));
    }

    [Theory]
    [InlineData("1.2.3.4", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("01.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("a.b.c.d", false)]
    public void Ipv4(string s, bool ok) => Assert.Equal(ok, Validate.Ipv4Valid(s));

    [Theory]
    [InlineData("::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("fe80::1:2:3:4", true)]
    [InlineData("1:2:3:4:5:6:7:8", true)]
    [InlineData("::ffff:1.2.3.4", true)]
    [InlineData("1:2:3:4:5:6:7:8:9", false)]
    [InlineData("1::2::3", false)]
    [InlineData("12345::", false)]
    [InlineData("g::1", false)]
    [InlineData(":", false)]
    public void Ipv6(string s, bool ok) => Assert.Equal(ok, Validate.Ipv6Valid(s));

    [Fact]
    public void Ipv6_ExpandsGroups()
    {
        Assert.Equal([0x2001, 0xdb8, 0, 0, 0, 0, 0, 1], Validate.Ipv6Parse("2001:db8::1"));
        Assert.Equal([0, 0, 0, 0, 0, 0xffff, 0x0102, 0x0304], Validate.Ipv6Parse("::ffff:1.2.3.4"));
    }

    [Theory]
    [InlineData("youtube.com", true)]
    [InlineData("^youtube.com", true)]
    [InlineData("a_b.example", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("Youtube.com", false)]
    [InlineData("*.youtube.com", false)]
    [InlineData("-a.com", false)]
    [InlineData("a-.com", false)]
    [InlineData("a..com", false)]
    [InlineData("пример.рф", false)]
    [InlineData("has space.com", false)]
    [InlineData("", false)]
    [InlineData("^", false)]
    public void Domain(string s, bool ok) => Assert.Equal(ok, Validate.DomainValid(s));

    [Fact]
    public void Domain_LabelTooLong()
    {
        Assert.False(Validate.DomainValid(new string('a', 64) + ".com"));
        Assert.True(Validate.DomainValid(new string('a', 63) + ".com"));
    }

    [Theory]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.4/32", "1.2.3.4")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("2001:DB8::/32", "2001:db8::/32")]
    [InlineData("::1", "::1")]
    public void Cidr_Normalizes(string s, string net) => Assert.Equal(net, Validate.CidrParse(s)!.Net);

    [Theory]
    [InlineData("1.2.3.4/33")]
    [InlineData("::1/129")]
    [InlineData("1.2.3.4/")]
    [InlineData("1.2.3.4/abc")]
    [InlineData("example.com")]
    public void Cidr_Rejects(string s) => Assert.Null(Validate.CidrParse(s));

    [Fact]
    public void PortFilter_ParsesAndMerges()
    {
        var p = Validate.ParsePortFilter("443,80,1000-2000,1500-3000")!;
        Assert.False(p.Negated);
        Assert.Equal(["80", "443", "1000-3000"], Validate.RangesToStrings(Validate.MergeRanges(p.Ranges)));
        Assert.True(Validate.ParsePortFilter("~443")!.Negated);
        Assert.Null(Validate.ParsePortFilter("70000"));
        Assert.Null(Validate.ParsePortFilter("5-1"));
        Assert.Null(Validate.ParsePortFilter("a"));
        Assert.Null(Validate.ParsePortFilter(""));
        Assert.Equal(["1-10"], Validate.RangesToStrings(Validate.MergeRanges([new(1, 5), new(6, 10)])));
    }

    [Theory]
    [InlineData("1024-65535", "1024-65535")]
    [InlineData("443,80", "80,443")]
    [InlineData("~80", null)]
    [InlineData("0-10", null)]
    [InlineData("x", null)]
    public void GamePorts(string v, string? expected) => Assert.Equal(expected, Validate.GamePorts(v));

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("2026.09.23", "2026.09.22", 1)]
    [InlineData("1.0.0-r1", "1.0.0-r2", -1)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("1.0a", "1.0b", -1)]
    [InlineData("99999999999999999999999999999999", "1", 1)]
    public void VersionCmp(string a, string b, int r) => Assert.Equal(r, Validate.VersionCmp(a, b));

    [Fact]
    public void Urls()
    {
        Assert.True(Validate.UrlValid("https://example.com/a?b=c"));
        Assert.True(Validate.UrlValid("http://example.com"));
        Assert.False(Validate.UrlValid("ftp://example.com"));
        Assert.False(Validate.UrlValid("https://exa mple.com"));
        Assert.True(Validate.HttpsUrlValid("https://example.com/x"));
        Assert.False(Validate.HttpsUrlValid("http://example.com/x"));
        Assert.True(Validate.Sha256Valid(new string('a', 64)));
        Assert.False(Validate.Sha256Valid(new string('A', 64)));
        Assert.True(Validate.SourceNameValid("refilter_domains"));
        Assert.False(Validate.SourceNameValid("Bad-Name"));
    }

    [Fact]
    public void ListText_ValidatesAndNormalizes()
    {
        var v = Validate.ValidateListText("hosts", "\uFEFFExample.COM\r\n# comment\n\n^strict.org\nbad domain\n*.x.com\n");
        Assert.False(v.Ok);
        Assert.Equal(2, v.ErrorCount);
        Assert.Equal("bad_domain", v.Errors[0].Reason);
        Assert.Equal(5, v.Errors[0].Line);
        var ok = Validate.ValidateListText("hosts", "Example.COM\r\n# c\n^strict.org\n");
        Assert.True(ok.Ok);
        Assert.Equal("example.com\n# c\n^strict.org\n", ok.Text);
        Assert.Equal(2, ok.Entries);
        var ip = Validate.ValidateListText("ipset", "10.1.1.1/8\n::1\n");
        Assert.Equal("10.0.0.0/8\n::1\n", ip.Text);
        Assert.Equal("bad_cidr", Validate.ValidateListText("ipset", "300.1.1.1\n").Errors[0].Reason);
        Assert.Equal("too_large", Validate.ValidateListText("hosts", new string('a', 20), 10).Errors[0].Reason);
        Assert.Equal("not_text", Validate.ValidateListText("hosts", null).Errors[0].Reason);
        Assert.Equal("bad_line", Validate.ValidateListText("hosts", new string('a', 300)).Errors[0].Reason);
        Assert.Equal("", Validate.ValidateListText("hosts", "").Text);
    }

    [Fact]
    public void CountEntries_SkipsComments()
    {
        Assert.Equal(2, Validate.CountEntries("a.com\n#x\n;y\n/z\n\nb.com\n"));
        Assert.Equal(0, Validate.CountEntries(null));
    }

    [Fact]
    public void ParseUint_Bounds()
    {
        Assert.Equal(5, Validate.ParseUint("5", 1, 10));
        Assert.Null(Validate.ParseUint("0", 1, 10));
        Assert.Null(Validate.ParseUint("-1", 0, 10));
        Assert.Null(Validate.ParseUint("1234567890", 0, int.MaxValue));
    }
}
