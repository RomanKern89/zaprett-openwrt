using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Zaprett.UpdateVerify;

namespace Zaprett.UpdateSign;

/// <summary>Key generation and signing; never part of the application (the service only verifies).</summary>
public static class Signer
{
    public const string KeyEnvironmentVariable = "ZAPRETT_UPDATE_KEY";

    public static string DefaultKeyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zaprett-keys", "update-ed25519.key");

    public static string ResolveKeyPath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        var fromEnv = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        return string.IsNullOrEmpty(fromEnv) ? DefaultKeyPath : fromEnv;
    }

    /// <summary>Returns (seed, publicKey) of a fresh key pair.</summary>
    public static (byte[] Seed, byte[] PublicKey) GenerateKeyPair()
    {
        var seed = RandomNumberGenerator.GetBytes(UpdateKeys.KeySize);
        return (seed, PublicKeyOf(seed));
    }

    public static byte[] PublicKeyOf(ReadOnlySpan<byte> seed) =>
        new Ed25519PrivateKeyParameters(seed.ToArray(), 0).GeneratePublicKey().GetEncoded();

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed.ToArray(), 0));
        var data = message.ToArray();
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    /// <summary>Writes the private key file readable only by the current user; never overwrites.</summary>
    public static void WritePrivateKeyFile(string path, ReadOnlySpan<byte> seed)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var text = System.Text.Encoding.ASCII.GetBytes(UpdateKeys.FormatPrivateKeyFile(seed));
        using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(text);
        }

        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(full).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
