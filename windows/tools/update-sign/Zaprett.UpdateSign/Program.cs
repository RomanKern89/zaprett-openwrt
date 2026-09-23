using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zaprett.UpdateSign;
using Zaprett.UpdateVerify;

const string Usage = """
    zaprett-update-sign — ed25519 signing of the Windows update manifest (update.json)

      keygen   [--key PATH] --pub PATH
               new key pair; the private key file is created only if it does not exist
      pubkey   [--key PATH]
               print the public key file of a private key
      manifest --msi FILE --version X.Y.Z --channel stable|beta --msi-url URL
               [--notes-url URL] [--released-at yyyy-MM-ddTHH:mm:ssZ] --out update.json
      sign     --manifest update.json [--key PATH] [--expect-pub PATH] [--out update.json.sig]
               sign the exact bytes of update.json; with --expect-pub the key must match that public key
      verify   --manifest update.json --sig update.json.sig [--pub PATH] [--msi FILE]
               without --pub the production key built into Zaprett.UpdateVerify is used

    Private key: --key, else the environment variable ZAPRETT_UPDATE_KEY (a path), else
    ~/.zaprett-keys/update-ed25519.key. It never belongs in the repository.
    Exit code: 0 ok, 1 verification or validation failed, 2 usage error.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

Dictionary<string, string> opts;
try
{
    opts = ParseOptions(args.AsSpan(1));
}
catch (ArgumentException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 2;
}

try
{
    return args[0] switch
    {
        "keygen" => KeyGen(opts),
        "pubkey" => PubKey(opts),
        "manifest" => MakeManifest(opts),
        "sign" => SignManifest(opts),
        "verify" => VerifyManifest(opts),
        _ => UsageError($"unknown command '{args[0]}'"),
    };
}
catch (UsageException e)
{
    return UsageError(e.Message);
}
catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or CryptographicException)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

static int UsageError(string message)
{
    Console.Error.WriteLine("error: " + message);
    Console.Error.WriteLine("run with --help for usage");
    return 2;
}

static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> rest)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < rest.Length; i++)
    {
        var name = rest[i];
        if (!name.StartsWith("--", StringComparison.Ordinal) || i + 1 >= rest.Length)
        {
            throw new ArgumentException($"expected --option value, got '{name}'");
        }

        if (!result.TryAdd(name[2..], rest[++i]))
        {
            throw new ArgumentException($"option {name} given twice");
        }
    }

    return result;
}

static string Require(Dictionary<string, string> opts, string name) =>
    opts.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new UsageException($"--{name} is required");

static string? Optional(Dictionary<string, string> opts, string name) => opts.GetValueOrDefault(name);

static byte[] ReadSeed(Dictionary<string, string> opts)
{
    var path = Signer.ResolveKeyPath(Optional(opts, "key"));
    if (!File.Exists(path))
    {
        throw new IOException($"private key not found: {path}");
    }

    return UpdateKeys.ParsePrivateKeyFile(File.ReadAllText(path, Encoding.ASCII));
}

static void WriteLf(string path, string text) => File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

static int KeyGen(Dictionary<string, string> opts)
{
    var keyPath = Signer.ResolveKeyPath(Optional(opts, "key"));
    var pubPath = Require(opts, "pub");
    if (File.Exists(keyPath))
    {
        Console.Error.WriteLine($"error: {keyPath} already exists; a key is never overwritten");
        return 1;
    }

    var (seed, pub) = Signer.GenerateKeyPair();
    Signer.WritePrivateKeyFile(keyPath, seed);
    WriteLf(pubPath, UpdateKeys.FormatPublicKeyFile(pub));
    Console.WriteLine($"private key: {Path.GetFullPath(keyPath)}");
    Console.WriteLine($"public key:  {Path.GetFullPath(pubPath)}");
    Console.WriteLine($"public key (base64): {Convert.ToBase64String(pub)}");
    return 0;
}

static int PubKey(Dictionary<string, string> opts)
{
    Console.Write(UpdateKeys.FormatPublicKeyFile(Signer.PublicKeyOf(ReadSeed(opts))));
    return 0;
}

static int MakeManifest(Dictionary<string, string> opts)
{
    var msi = Require(opts, "msi");
    string sha;
    long size;
    using (var fs = File.OpenRead(msi))
    {
        size = fs.Length;
        sha = Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    var releasedText = Optional(opts, "released-at");
    var releasedAt = releasedText is null
        ? DateTimeOffset.UtcNow
        : DateTimeOffset.ParseExact(releasedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    var notes = Optional(opts, "notes-url");
    var draft = new UpdateManifest(Require(opts, "version"), Require(opts, "channel"), new Uri(Require(opts, "msi-url")),
        sha, size, new DateTimeOffset(releasedAt.UtcDateTime.AddTicks(-(releasedAt.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero),
        notes is null ? null : new Uri(notes));
    var bytes = draft.ToJsonBytes();
    UpdateManifest.Parse(bytes);   // the same validation the service applies
    File.WriteAllBytes(Require(opts, "out"), bytes);
    Console.WriteLine($"update.json: version {draft.Version}, sha256 {sha}, size {size}");
    return 0;
}

static int SignManifest(Dictionary<string, string> opts)
{
    var manifestPath = Require(opts, "manifest");
    var manifest = File.ReadAllBytes(manifestPath);
    var seed = ReadSeed(opts);
    var pub = Signer.PublicKeyOf(seed);
    if (Optional(opts, "expect-pub") is { } expectPath)
    {
        var expected = UpdateKeys.ParsePublicKeyFile(File.ReadAllText(expectPath, Encoding.ASCII));
        if (!expected.AsSpan().SequenceEqual(pub))
        {
            Console.Error.WriteLine($"error: the private key does not belong to {expectPath}");
            return 1;
        }
    }

    var sigText = UpdateVerifier.FormatSignature(Signer.Sign(seed, manifest));
    var check = UpdateVerifier.Verify(manifest, sigText, pub);
    if (!check.IsValid)
    {
        Console.Error.WriteLine($"error: the signed manifest does not verify: {check.Status} {check.Error}");
        return 1;
    }

    var outPath = Optional(opts, "out") ?? manifestPath + ".sig";
    WriteLf(outPath, sigText);
    Console.WriteLine($"signed: {outPath}");
    return 0;
}

static int VerifyManifest(Dictionary<string, string> opts)
{
    var manifest = File.ReadAllBytes(Require(opts, "manifest"));
    var sigText = File.ReadAllText(Require(opts, "sig"), Encoding.ASCII);
    var result = Optional(opts, "pub") is { } pubPath
        ? UpdateVerifier.Verify(manifest, sigText, UpdateKeys.ParsePublicKeyFile(File.ReadAllText(pubPath, Encoding.ASCII)))
        : UpdateVerifier.Verify(manifest, sigText);
    if (!result.IsValid)
    {
        Console.Error.WriteLine($"FAIL: {result.Status}: {result.Error}");
        return 1;
    }

    Console.WriteLine($"OK: signature valid, version {result.Manifest!.Version} ({result.Manifest.Channel})");
    if (Optional(opts, "msi") is { } msiPath)
    {
        using var fs = File.OpenRead(msiPath);
        if (!UpdateVerifier.MatchesFile(result.Manifest, fs))
        {
            Console.Error.WriteLine($"FAIL: {msiPath} does not match sha256/size of the manifest");
            return 1;
        }

        Console.WriteLine($"OK: {msiPath} matches sha256 and size");
    }

    return 0;
}

internal sealed class UsageException(string message) : Exception(message);
