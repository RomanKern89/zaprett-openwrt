using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Zaprett.UpdateVerify;

public enum VerifyStatus
{
    Ok,
    BadKey,
    BadSignatureFormat,
    BadSignature,
    BadManifest,
}

public sealed record VerifyResult(VerifyStatus Status, UpdateManifest? Manifest, string? Error)
{
    public bool IsValid => Status == VerifyStatus.Ok && Manifest is not null;
}

/// <summary>
/// Checks <c>update.json</c> against its detached signature <c>update.json.sig</c>
/// (one line: base64 of the 64-byte ed25519 signature over the exact bytes of update.json).
/// The manifest is parsed only when the signature is valid.
/// </summary>
public static class UpdateVerifier
{
    public const int SignatureSize = 64;

    /// <summary>Largest update.json accepted before any parsing.</summary>
    public const int MaxManifestBytes = 16 * 1024;

    public static VerifyResult Verify(ReadOnlySpan<byte> manifest, string signatureText) =>
        Verify(manifest, signatureText, UpdateKeys.ProductionPublicKey);

    public static VerifyResult Verify(ReadOnlySpan<byte> manifest, string signatureText, ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != UpdateKeys.KeySize)
        {
            return new VerifyResult(VerifyStatus.BadKey, null, $"public key must be {UpdateKeys.KeySize} bytes");
        }

        if (manifest.Length == 0 || manifest.Length > MaxManifestBytes)
        {
            return new VerifyResult(VerifyStatus.BadManifest, null, $"update.json must be 1..{MaxManifestBytes} bytes");
        }

        if (!TryDecodeSignature(signatureText, out var signature, out var sigError))
        {
            return new VerifyResult(VerifyStatus.BadSignatureFormat, null, sigError);
        }

        bool valid;
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
            var data = manifest.ToArray();
            signer.BlockUpdate(data, 0, data.Length);
            valid = signer.VerifySignature(signature);
        }
        catch (ArgumentException e)
        {
            return new VerifyResult(VerifyStatus.BadKey, null, e.Message);
        }

        if (!valid)
        {
            return new VerifyResult(VerifyStatus.BadSignature, null, "signature does not match update.json");
        }

        try
        {
            return new VerifyResult(VerifyStatus.Ok, UpdateManifest.Parse(manifest), null);
        }
        catch (FormatException e)
        {
            return new VerifyResult(VerifyStatus.BadManifest, null, e.Message);
        }
    }

    /// <summary>Formats a signature the way update.json.sig stores it.</summary>
    public static string FormatSignature(ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureSize)
        {
            throw new ArgumentException($"signature must be {SignatureSize} bytes", nameof(signature));
        }

        return Convert.ToBase64String(signature) + "\n";
    }

    /// <summary>Streams a downloaded MSI and compares its sha256 and size with the verified manifest.</summary>
    public static bool MatchesFile(UpdateManifest manifest, Stream content)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(content);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > manifest.Size)
            {
                return false;
            }

            sha.AppendData(buffer, 0, read);
        }

        var hex = Convert.ToHexStringLower(sha.GetHashAndReset());
        return total == manifest.Size && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(hex), Encoding.ASCII.GetBytes(manifest.Sha256));
    }

    private static bool TryDecodeSignature(string? text, out byte[] signature, out string? error)
    {
        signature = [];
        error = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 128 || trimmed.Contains('\n', StringComparison.Ordinal))
        {
            error = "update.json.sig must be one line of base64";
            return false;
        }

        try
        {
            signature = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            error = "update.json.sig is not valid base64";
            return false;
        }

        if (signature.Length != SignatureSize)
        {
            error = $"signature must be {SignatureSize} bytes, got {signature.Length}";
            return false;
        }

        return true;
    }
}
