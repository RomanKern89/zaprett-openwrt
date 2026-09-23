using System.Globalization;
using System.Text.RegularExpressions;
using Zaprett.Core.Util;

namespace Zaprett.Core.Validation;

/// <summary>A port range [Lo, Hi] (inclusive).</summary>
public readonly record struct PortRange(int Lo, int Hi)
{
    public override string ToString() => Lo == Hi ? Lo.ToString(CultureInfo.InvariantCulture) : $"{Lo}-{Hi}";
}

public sealed record PortFilter(bool Negated, IReadOnlyList<PortRange> Ranges);

public sealed record Cidr(int Family, string Addr, int Prefix, string Net);

public sealed record ListError(int Line, string Value, string Reason);

public sealed record ListValidation(bool Ok, string Text, int Entries, IReadOnlyList<ListError> Errors, int ErrorCount);

/// <summary>Pure validation and parsing helpers, port of validate.uc (router) — same rules, same limits.</summary>
public static partial class Validate
{
    public const int MaxUserListBytes = 1048576;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdChars();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex Ipv4Shape();

    [GeneratedRegex("^[0-9a-fA-F:.]+$")]
    private static partial Regex Ipv6Chars();

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex Hex();

    [GeneratedRegex("^[a-z0-9_.-]+$")]
    private static partial Regex HostChars();

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex Digits();

    [GeneratedRegex("^([0-9]{1,5})(-([0-9]{1,5}))?$")]
    private static partial Regex PortItem();

    [GeneratedRegex("^https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?(/[A-Za-z0-9._~%!$&'()*+,;=:@/?#-]*)?$")]
    private static partial Regex UrlShape();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+~_-]*$")]
    private static partial Regex VersionShape();

    [GeneratedRegex("^[0-9a-f]+$")]
    private static partial Regex LowerHex();

    [GeneratedRegex("[.+~_-]")]
    private static partial Regex VersionSep();

    /// <summary>Item id: 1..96 characters of [A-Za-z0-9._-], not starting with '.' (router util.is_id).</summary>
    public static bool IsId(string? s) =>
        s is { Length: >= 1 and <= 96 } && s[0] != '.' && IdChars().IsMatch(s);

    public static int[]? Ipv4Parse(string? s)
    {
        if (s == null || !Ipv4Shape().IsMatch(s))
            return null;
        var parts = s.Split('.');
        var r = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var p = parts[i];
            if (p.Length > 3 || (p.Length > 1 && p[0] == '0'))
                return null;
            var n = int.Parse(p, CultureInfo.InvariantCulture);
            if (n > 255)
                return null;
            r[i] = n;
        }
        return r;
    }

    public static bool Ipv4Valid(string? s) => Ipv4Parse(s) != null;

    public static int[]? Ipv6Parse(string? s)
    {
        if (s == null || s.Length < 2 || s.Length > 45 || !Ipv6Chars().IsMatch(s))
            return null;
        string head = s;
        string? tail = null;
        var dc = s.IndexOf("::", StringComparison.Ordinal);
        if (dc >= 0)
        {
            head = s[..dc];
            tail = s[(dc + 2)..];
            if (tail.Contains("::", StringComparison.Ordinal))
                return null;
        }
        List<int>? Groups(string part, bool allowV4)
        {
            var o = new List<int>();
            if (part.Length == 0)
                return o;
            var items = part.Split(':');
            for (var i = 0; i < items.Length; i++)
            {
                var g = items[i];
                if (allowV4 && i == items.Length - 1 && g.Contains('.', StringComparison.Ordinal))
                {
                    var v4 = Ipv4Parse(g);
                    if (v4 == null)
                        return null;
                    o.Add(v4[0] * 256 + v4[1]);
                    o.Add(v4[2] * 256 + v4[3]);
                    continue;
                }
                if (g.Length < 1 || g.Length > 4 || !Hex().IsMatch(g))
                    return null;
                o.Add(int.Parse(g, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            return o;
        }
        if (tail == null)
        {
            var hg = Groups(head, true);
            return hg is { Count: 8 } ? hg.ToArray() : null;
        }
        var h = Groups(head, false);
        var t = Groups(tail, true);
        if (h == null || t == null || h.Count + t.Count > 7)
            return null;
        var res = new List<int>(h);
        for (var i = h.Count + t.Count; i < 8; i++)
            res.Add(0);
        res.AddRange(t);
        return res.ToArray();
    }

    public static bool Ipv6Valid(string? s) => Ipv6Parse(s) != null;

    public static bool HostnameLabelsValid(string d)
    {
        var n = d.Length;
        if (n == 0 || n > 253 || !HostChars().IsMatch(d))
            return false;
        char first = d[0], last = d[n - 1];
        if (first == '.' || first == '-' || last == '.' || last == '-' || d.Contains("..", StringComparison.Ordinal) ||
            d.Contains(".-", StringComparison.Ordinal) || d.Contains("-.", StringComparison.Ordinal))
            return false;
        if (n > 63)
            foreach (var l in d.Split('.'))
                if (l.Length > 63)
                    return false;
        return true;
    }

    /// <summary>Host name as the engine matches it (lower case), optional leading '^'; IP literals accepted.</summary>
    public static bool DomainValid(string? s)
    {
        if (s == null)
            return false;
        var d = s.StartsWith('^') ? s[1..] : s;
        if (d.Length == 0 || d.Length > 253)
            return false;
        return HostnameLabelsValid(d) || Ipv4Valid(d) || Ipv6Valid(d);
    }

    public static Cidr? CidrParse(string? s)
    {
        if (s == null)
            return null;
        var addr = s;
        int? prefix = null;
        var slash = s.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            addr = s[..slash];
            var p = s[(slash + 1)..];
            if (p.Length < 1 || p.Length > 3 || !Digits().IsMatch(p))
                return null;
            prefix = int.Parse(p, CultureInfo.InvariantCulture);
        }
        var v4 = Ipv4Parse(addr);
        if (v4 != null)
        {
            var pr = prefix ?? 32;
            if (pr > 32)
                return null;
            var num = ((uint)v4[0] << 24) | ((uint)v4[1] << 16) | ((uint)v4[2] << 8) | (uint)v4[3];
            var mask = pr == 0 ? 0u : 0xffffffffu << (32 - pr);
            var net = num & mask;
            var ns = string.Create(CultureInfo.InvariantCulture, $"{net >> 24 & 255}.{net >> 16 & 255}.{net >> 8 & 255}.{net & 255}");
            return new Cidr(4, addr, pr, pr == 32 ? ns : $"{ns}/{pr}");
        }
        if (Ipv6Parse(addr) != null)
        {
            var pr = prefix ?? 128;
            if (pr > 128)
                return null;
            var a = addr.ToLowerInvariant();
            return new Cidr(6, a, pr, pr == 128 ? a : $"{a}/{pr}");
        }
        return null;
    }

    public static int? ParseUint(string? s, int min, int max)
    {
        if (s == null || s.Length < 1 || s.Length > 9 || !Digits().IsMatch(s))
            return null;
        var n = int.Parse(s, CultureInfo.InvariantCulture);
        return n < min || n > max ? null : n;
    }

    /// <summary>Validates the text of a user list. kind: "hosts" or "ipset". Comments ('#') are kept.</summary>
    public static ListValidation ValidateListText(string kind, string? text, int maxBytes = MaxUserListBytes)
    {
        if (text == null)
            return new ListValidation(false, "", 0, [new ListError(0, "", "not_text")], 1);
        if (Files.Utf8NoBom.GetByteCount(text) > maxBytes)
            return new ListValidation(false, "", 0, [new ListError(0, "", "too_large")], 1);
        var o = new List<string>();
        var errors = new List<ListError>();
        var entries = 0;
        var lines = StripBom(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0)
                continue;
            if (l[0] == '#')
            {
                o.Add(l);
                continue;
            }
            if (l.Contains('\0', StringComparison.Ordinal) || l.Length > 255)
            {
                errors.Add(new ListError(i + 1, Head(l), "bad_line"));
                continue;
            }
            if (kind == "hosts")
            {
                var d = l.ToLowerInvariant();
                if (!DomainValid(d))
                {
                    errors.Add(new ListError(i + 1, Head(l), "bad_domain"));
                    continue;
                }
                o.Add(d);
            }
            else
            {
                var c = CidrParse(l);
                if (c == null)
                {
                    errors.Add(new ListError(i + 1, Head(l), "bad_cidr"));
                    continue;
                }
                o.Add(c.Net);
            }
            entries++;
        }
        return new ListValidation(errors.Count == 0, o.Count > 0 ? string.Join('\n', o) + "\n" : "", entries,
            errors.Take(50).ToList(), errors.Count);
    }

    static string Head(string l) => l.Length > 64 ? l[..64] : l;

    public static string StripBom(string s) => s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;

    /// <summary>Entries of a plain text list, counted the way the engine skips comments.</summary>
    public static int CountEntries(string? text)
    {
        var n = 0;
        foreach (var l in (text ?? "").Split('\n'))
        {
            if (l.Length == 0)
                continue;
            var c = l[0];
            if (c == '#' || c == ';' || c == '/' || c == '\r')
                continue;
            n++;
        }
        return n;
    }

    public static int VersionCmp(string? a, string? b)
    {
        var pa = VersionSep().Split(a ?? "");
        var pb = VersionSep().Split(b ?? "");
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < pa.Length ? pa[i] : "0";
            var y = i < pb.Length ? pb[i] : "0";
            var xn = Digits().IsMatch(x);
            var yn = Digits().IsMatch(y);
            if (xn && yn)
            {
                var d = CompareDigits(x, y);
                if (d != 0)
                    return d;
            }
            else if (x != y)
            {
                if (xn)
                    return 1;
                if (yn)
                    return -1;
                return string.CompareOrdinal(x, y) > 0 ? 1 : -1;
            }
        }
        return 0;
    }

    static int CompareDigits(string x, string y)
    {
        x = x.TrimStart('0');
        y = y.TrimStart('0');
        if (x.Length != y.Length)
            return x.Length > y.Length ? 1 : -1;
        var c = string.CompareOrdinal(x, y);
        return c == 0 ? 0 : (c > 0 ? 1 : -1);
    }

    public static bool VersionValid(string? v) => v is { Length: >= 1 and <= 32 } && VersionShape().IsMatch(v);

    /// <summary>Engine port filter "[~]p1[-p2],...".</summary>
    public static PortFilter? ParsePortFilter(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var negated = false;
        var ranges = new List<PortRange>();
        foreach (var raw in value.Split(','))
        {
            var item = raw;
            if (item.StartsWith('~'))
            {
                negated = true;
                item = item[1..];
            }
            var m = PortItem().Match(item);
            if (!m.Success)
                return null;
            var lo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var hi = m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : lo;
            if (lo > 65535 || hi > 65535 || lo > hi)
                return null;
            ranges.Add(new PortRange(lo, hi));
        }
        return new PortFilter(negated, ranges);
    }

    public static List<PortRange> MergeRanges(IEnumerable<PortRange> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Lo).ThenBy(r => r.Hi).ToList();
        var o = new List<PortRange>();
        foreach (var r in sorted)
        {
            if (o.Count > 0 && r.Lo <= o[^1].Hi + 1)
            {
                if (r.Hi > o[^1].Hi)
                    o[^1] = o[^1] with { Hi = r.Hi };
            }
            else
            {
                o.Add(r);
            }
        }
        return o;
    }

    public static List<string> RangesToStrings(IEnumerable<PortRange> ranges) => ranges.Select(r => r.ToString()).ToList();

    /// <summary>Ports of the game filter: no negation, no port 0; merged "a,b-c" or null.</summary>
    public static string? GamePorts(string? value)
    {
        var p = ParsePortFilter(value);
        if (p == null || p.Negated || p.Ranges.Any(r => r.Lo < 1))
            return null;
        return string.Join(',', RangesToStrings(MergeRanges(p.Ranges)));
    }

    public static bool UrlValid(string? u) => u is { Length: <= 2048 } && UrlShape().IsMatch(u);

    /// <summary>Addresses configuration is downloaded from (repository index, subscriptions): https only.</summary>
    public static bool HttpsUrlValid(string? u) => UrlValid(u) && u!.StartsWith("https://", StringComparison.Ordinal);

    public static bool Sha256Valid(string? s) => s is { Length: 64 } && LowerHex().IsMatch(s);

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex SourceNameChars();

    /// <summary>Subscription name: ^[a-z0-9_]{1,32}$.</summary>
    public static bool SourceNameValid(string? s) => s is { Length: >= 1 and <= 32 } && SourceNameChars().IsMatch(s);
}
