using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zaprett.UpdateVerify;

/// <summary>
/// Application update manifest (<c>update.json</c>), published next to the MSI and signed with ed25519
/// (ARCHITECTURE-WIN W-7). Parsed only after the signature over its exact bytes has been verified.
/// </summary>
public sealed record UpdateManifest(
    string Version,
    string Channel,
    Uri MsiUrl,
    string Sha256,
    long Size,
    DateTimeOffset ReleasedAt,
    Uri? NotesUrl)
{
    public static readonly IReadOnlyList<string> Channels = ["stable", "beta"];

    /// <summary>Largest MSI the service agrees to download (the target size is 60 MB).</summary>
    public const long MaxSize = 512L * 1024 * 1024;

    private static readonly Regex VersionPattern = new(@"^\d{1,5}\.\d{1,5}\.\d{1,5}(-[0-9A-Za-z.]{1,32})?$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>Parses and validates the manifest; throws <see cref="FormatException"/> with the reason.</summary>
    public static UpdateManifest Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException e)
        {
            throw new FormatException("update.json is not valid JSON: " + e.Message, e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("update.json must be a JSON object");
            }

            var version = RequireString(root, "version");
            if (!VersionPattern.IsMatch(version))
            {
                throw new FormatException($"version '{version}' is not MAJOR.MINOR.PATCH[-suffix]");
            }

            var channel = RequireString(root, "channel");
            if (!Channels.Contains(channel))
            {
                throw new FormatException($"channel '{channel}' is not one of: {string.Join(", ", Channels)}");
            }

            var msiUrl = RequireHttps(RequireString(root, "msi_url"), "msi_url");
            if (!msiUrl.AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("msi_url must point to a .msi file");
            }

            var sha256 = RequireString(root, "sha256");
            if (!Sha256Pattern.IsMatch(sha256))
            {
                throw new FormatException("sha256 must be 64 lowercase hex characters");
            }

            if (!root.TryGetProperty("size", out var sizeEl) || sizeEl.ValueKind != JsonValueKind.Number
                || !sizeEl.TryGetInt64(out var size) || size <= 0 || size > MaxSize)
            {
                throw new FormatException($"size must be an integer in 1..{MaxSize}");
            }

            var releasedText = RequireString(root, "released_at");
            if (!DateTimeOffset.TryParseExact(releasedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var releasedAt))
            {
                throw new FormatException("released_at must be UTC in the form yyyy-MM-ddTHH:mm:ssZ");
            }

            Uri? notesUrl = null;
            if (root.TryGetProperty("notes_url", out var notesEl) && notesEl.ValueKind != JsonValueKind.Null)
            {
                if (notesEl.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("notes_url must be a string");
                }

                notesUrl = RequireHttps(notesEl.GetString()!, "notes_url");
            }

            return new UpdateManifest(version, channel, msiUrl, sha256, size, releasedAt, notesUrl);
        }
    }

    /// <summary>Serializes the manifest in the canonical form produced by the signing tool (UTF-8, LF, no BOM).</summary>
    public byte[] ToJsonBytes()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("version", Version);
            w.WriteString("channel", Channel);
            w.WriteString("msi_url", MsiUrl.AbsoluteUri);
            w.WriteString("sha256", Sha256);
            w.WriteNumber("size", Size);
            w.WriteString("released_at", ReleasedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (NotesUrl is null)
            {
                w.WriteNull("notes_url");
            }
            else
            {
                w.WriteString("notes_url", NotesUrl.AbsoluteUri);
            }

            w.WriteEndObject();
        }

        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"{name} is missing or not a string");
        }

        var value = el.GetString()!;
        if (value.Length == 0 || value.Length > 2048)
        {
            throw new FormatException($"{name} is empty or too long");
        }

        return value;
    }

    private static Uri RequireHttps(string text, string name)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new FormatException($"{name} must be an absolute https URL without credentials");
        }

        return uri;
    }
}
