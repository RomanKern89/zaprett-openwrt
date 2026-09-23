using System.Security.Cryptography;
using System.Text;
using Zaprett.UpdateSign;

namespace Zaprett.UpdateVerify.Tests;

public class UpdateVerifierTests
{
    private const string ManifestJson = """
        {
          "version": "1.2.3",
          "channel": "stable",
          "msi_url": "https://github.com/example/zaprett/releases/download/win-v1.2.3/zaprett-1.2.3-x64.msi",
          "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "size": 12345,
          "released_at": "2026-09-23T10:00:00Z",
          "notes_url": "https://github.com/example/zaprett/releases/tag/win-v1.2.3"
        }
        """;

    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes(ManifestJson);

    private static (byte[] Seed, byte[] Pub, string Sig) SignedWithFreshKey(byte[] manifest)
    {
        var (seed, pub) = Signer.GenerateKeyPair();
        return (seed, pub, UpdateVerifier.FormatSignature(Signer.Sign(seed, manifest)));
    }

    [Fact]
    public void ValidSignatureVerifiesAndParsesManifest()
    {
        var (_, pub, sig) = SignedWithFreshKey(Manifest);

        var result = UpdateVerifier.Verify(Manifest, sig, pub);

        Assert.Equal(VerifyStatus.Ok, result.Status);
        Assert.True(result.IsValid);
        Assert.Equal("1.2.3", result.Manifest!.Version);
        Assert.Equal("stable", result.Manifest.Channel);
        Assert.Equal(12345, result.Manifest.Size);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero), result.Manifest.ReleasedAt);
    }

    [Fact]
    public void EveryFlippedManifestByteIsRejected()
    {
        var (_, pub, sig) = SignedWithFreshKey(Manifest);

        for (var i = 0; i < Manifest.Length; i++)
        {
            var tampered = (byte[])Manifest.Clone();
            tampered[i] ^= 0x01;
            var result = UpdateVerifier.Verify(tampered, sig, pub);
            Assert.False(result.IsValid, $"byte {i} flipped but accepted");
            Assert.Equal(VerifyStatus.BadSignature, result.Status);
        }
    }

    [Fact]
    public void EveryFlippedSignatureBitIsRejected()
    {
        var (seed, pub, _) = SignedWithFreshKey(Manifest);
        var raw = Signer.Sign(seed, Manifest);

        for (var i = 0; i < raw.Length * 8; i++)
        {
            var tampered = (byte[])raw.Clone();
            tampered[i / 8] ^= (byte)(1 << (i % 8));
            var result = UpdateVerifier.Verify(Manifest, UpdateVerifier.FormatSignature(tampered), pub);
            Assert.False(result.IsValid, $"signature bit {i} flipped but accepted");
        }
    }

    [Fact]
    public void AppendedWhitespaceInManifestIsRejected()
    {
        var (_, pub, sig) = SignedWithFreshKey(Manifest);
        var longer = Manifest.Concat("\n"u8.ToArray()).ToArray();

        Assert.Equal(VerifyStatus.BadSignature, UpdateVerifier.Verify(longer, sig, pub).Status);
    }

    [Fact]
    public void OtherKeyIsRejected()
    {
        var (_, _, sig) = SignedWithFreshKey(Manifest);
        var (_, otherPub) = Signer.GenerateKeyPair();

        Assert.Equal(VerifyStatus.BadSignature, UpdateVerifier.Verify(Manifest, sig, otherPub).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    [InlineData("line1\nline2")]
    public void MalformedSignatureIsRejected(string sig)
    {
        var (_, pub) = Signer.GenerateKeyPair();

        Assert.Equal(VerifyStatus.BadSignatureFormat, UpdateVerifier.Verify(Manifest, sig, pub).Status);
    }

    [Fact]
    public void WrongKeyLengthIsRejected()
    {
        var (_, _, sig) = SignedWithFreshKey(Manifest);

        Assert.Equal(VerifyStatus.BadKey, UpdateVerifier.Verify(Manifest, sig, new byte[31]).Status);
    }

    [Fact]
    public void OversizedManifestIsRejectedBeforeCrypto()
    {
        var big = new byte[UpdateVerifier.MaxManifestBytes + 1];
        var (_, pub, sig) = SignedWithFreshKey(big);

        Assert.Equal(VerifyStatus.BadManifest, UpdateVerifier.Verify(big, sig, pub).Status);
    }

    [Theory]
    [InlineData("\"channel\": \"stable\"", "\"channel\": \"nightly\"")]
    [InlineData("\"version\": \"1.2.3\"", "\"version\": \"1.2\"")]
    [InlineData("https://github.com/example/zaprett/releases/download", "http://github.com/example/zaprett/releases/download")]
    [InlineData("zaprett-1.2.3-x64.msi", "zaprett-1.2.3-x64.exe")]
    [InlineData("\"size\": 12345", "\"size\": 0")]
    [InlineData("\"size\": 12345", "\"size\": \"12345\"")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("2026-09-23T10:00:00Z", "2026-09-23 10:00")]
    [InlineData("https://github.com/example/zaprett/releases/tag", "https://user:pw@github.com/example/zaprett/releases/tag")]
    public void SignedButInvalidManifestIsRejected(string from, string to)
    {
        var bad = Encoding.UTF8.GetBytes(ManifestJson.Replace(from, to, StringComparison.Ordinal));
        Assert.NotEqual(Manifest, bad);
        var (_, pub, sig) = SignedWithFreshKey(bad);

        var result = UpdateVerifier.Verify(bad, sig, pub);

        Assert.Equal(VerifyStatus.BadManifest, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void CanonicalJsonRoundTrips()
    {
        var parsed = UpdateManifest.Parse(Manifest);
        var bytes = parsed.ToJsonBytes();

        Assert.Equal(parsed, UpdateManifest.Parse(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    [Fact]
    public void MatchesFileChecksShaAndSize()
    {
        var payload = RandomNumberGenerator.GetBytes(5000);
        var good = new UpdateManifest("1.0.0", "stable", new Uri("https://example.org/z.msi"),
            Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length, DateTimeOffset.UnixEpoch, null);

        Assert.True(UpdateVerifier.MatchesFile(good, new MemoryStream(payload)));

        var flipped = (byte[])payload.Clone();
        flipped[4999] ^= 0x80;
        Assert.False(UpdateVerifier.MatchesFile(good, new MemoryStream(flipped)));
        Assert.False(UpdateVerifier.MatchesFile(good, new MemoryStream(payload.Concat(new byte[] { 0 }).ToArray())));
        Assert.False(UpdateVerifier.MatchesFile(good with { Size = payload.Length + 1 }, new MemoryStream(payload)));
    }

    [Fact]
    public void KeyFilesRoundTripAndRejectGarbage()
    {
        var (seed, pub) = Signer.GenerateKeyPair();

        Assert.Equal(seed, UpdateKeys.ParsePrivateKeyFile(UpdateKeys.FormatPrivateKeyFile(seed)));
        Assert.Equal(pub, UpdateKeys.ParsePublicKeyFile(UpdateKeys.FormatPublicKeyFile(pub).Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => UpdateKeys.ParsePublicKeyFile(UpdateKeys.FormatPrivateKeyFile(seed)));
        Assert.Throws<FormatException>(() => UpdateKeys.ParsePublicKeyFile(UpdateKeys.PublicHeader + "\nAAAA\n"));
        // the leak scanners of the publication (make_public_tree.py) look for this marker
        Assert.Contains("PRIVATE" + " KEY", UpdateKeys.FormatPrivateKeyFile(seed), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionKeyEqualsCommittedPublicKeyFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "keys", "update-ed25519.pub");
        var committed = UpdateKeys.ParsePublicKeyFile(File.ReadAllText(path, Encoding.ASCII));

        Assert.Equal(committed, UpdateKeys.ProductionPublicKey);
    }

    [Fact]
    public void ProductionKeyDoesNotAcceptTestSignatures()
    {
        var (_, _, sig) = SignedWithFreshKey(Manifest);

        Assert.Equal(VerifyStatus.BadSignature, UpdateVerifier.Verify(Manifest, sig).Status);
    }
}
