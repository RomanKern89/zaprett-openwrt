using System.Text;

namespace Zaprett.Cli;

/// <summary>
/// Text from stdin, read as bytes whatever the console encoding is: Windows PowerShell 5.1 pipes a string with
/// $OutputEncoding (ASCII by default, UTF-8 with a BOM or UTF-16 after users change it), cmd redirection gives the
/// file bytes as they are. Recognized: UTF-8 with and without BOM, UTF-16 LE/BE with BOM, UTF-16 LE without BOM (a NUL
/// in every second byte); anything else that is not valid UTF-8 is read in the OEM code page. The BOM and trailing
/// whitespace and line breaks are removed.
/// </summary>
public static class StdinText
{
    /// <summary>Reads at most <paramref name="limit"/> bytes; null when the input is longer.</summary>
    public static async Task<string?> ReadAsync(Stream input, int limit, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > limit)
                return null;
        }
        return Decode(ms.ToArray());
    }

    public static string Decode(byte[] bytes)
    {
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else if (LooksLikeUtf16Le(bytes))
            text = Encoding.Unicode.GetString(bytes);
        else
            text = TryUtf8(bytes) ?? Oem().GetString(bytes);
        // a BOM can survive inside (UTF-16 text that carried a UTF-8 BOM, or a doubled one)
        return text.TrimStart('\uFEFF').TrimEnd(' ', '\t', '\r', '\n', '\0', '\uFEFF');
    }

    // ASCII-range UTF-16 LE without a BOM: every odd byte is NUL
    private static bool LooksLikeUtf16Le(byte[] b)
    {
        if (b.Length < 4 || b.Length % 2 != 0)
            return false;
        int zeros = 0;
        for (int i = 1; i < b.Length; i += 2)
            if (b[i] == 0)
                zeros++;
        return zeros * 2 >= b.Length / 2 * 1.8;   // 90 % of the odd bytes
    }

    private static string? TryUtf8(byte[] b)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static Encoding Oem()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }
}
