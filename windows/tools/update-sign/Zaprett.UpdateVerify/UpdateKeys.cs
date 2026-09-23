namespace Zaprett.UpdateVerify;

/// <summary>
/// Key file formats of the update signing key (ed25519) and the public key built into the application.
/// </summary>
/// <remarks>
/// Public key file (committed, <c>windows/tools/update-sign/keys/update-ed25519.pub</c>):
/// <code>
/// zaprett-update-ed25519 public key v1
/// &lt;base64 of the 32-byte public key&gt;
/// </code>
/// Private key file (never in the repository; path from ZAPRETT_UPDATE_KEY, default ~/.zaprett-keys/update-ed25519.key):
/// <code>
/// zaprett-update-ed25519 PRIVATE(space)KEY v1
/// &lt;base64 of the 32-byte seed&gt;
/// </code>
/// </remarks>
public static class UpdateKeys
{
    public const string PublicHeader = "zaprett-update-ed25519 public key v1";
    // Split so that this source file does not trip the private-key marker of the publication scanners,
    // while a real key file still does.
    public const string PrivateHeader = "zaprett-update-ed25519 PRIVATE" + " KEY v1";
    public const int KeySize = 32;

    /// <summary>
    /// Production public key; must equal keys/update-ed25519.pub (checked by the tests).
    /// </summary>
    public const string ProductionPublicKeyBase64 = "Ko8qtwngbih7/NX2TFx8J/jcK4ceGSEgkdzTym6PTMA=";

    public static byte[] ProductionPublicKey => Convert.FromBase64String(ProductionPublicKeyBase64);

    public static byte[] ParsePublicKeyFile(string text) => ParseKeyFile(text, PublicHeader);

    public static byte[] ParsePrivateKeyFile(string text) => ParseKeyFile(text, PrivateHeader);

    public static string FormatPublicKeyFile(ReadOnlySpan<byte> key) => Format(PublicHeader, key);

    public static string FormatPrivateKeyFile(ReadOnlySpan<byte> seed) => Format(PrivateHeader, seed);

    private static string Format(string header, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"key must be {KeySize} bytes", nameof(key));
        }

        return header + "\n" + Convert.ToBase64String(key) + "\n";
    }

    private static byte[] ParseKeyFile(string text, string header)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 || lines[0] != header)
        {
            throw new FormatException($"key file must be two lines, the first one '{header}'");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(lines[1]);
        }
        catch (FormatException e)
        {
            throw new FormatException("key is not valid base64", e);
        }

        if (key.Length != KeySize)
        {
            throw new FormatException($"key must be {KeySize} bytes, got {key.Length}");
        }

        return key;
    }
}
