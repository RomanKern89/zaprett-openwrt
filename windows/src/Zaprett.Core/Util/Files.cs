using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zaprett.Core.Util;

/// <summary>File helpers: size-limited reads and atomic writes (tmp + File.Replace, optional .bak).</summary>
public static class Files
{
    public static readonly UTF8Encoding Utf8NoBom = new(false);

    // LF on every platform: the files are compared and shipped as LF (.editorconfig), Environment.NewLine is CRLF here
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, NewLine = "\n" };

    /// <summary>Reads a text file; null when missing, not a file or larger than the limit.</summary>
    public static string? ReadLimited(string path, long limit)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > limit)
                return null;
            // shared read: an atomic replace of the file by a writer must not fail because a reader has it open
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8, true);
            return sr.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static JsonObject? ReadJson(string path, long limit = 1048576) => R.ParseObject(ReadLimited(path, limit));

    /// <summary>Writes a temporary file in the same directory and moves it over the target. With
    /// <paramref name="backup"/> the previous version is kept as <c>&lt;path&gt;.bak</c> (File.Replace).</summary>
    public static bool AtomicWrite(string path, byte[] data, bool backup = false)
    {
        // a scanner or a reader without FILE_SHARE_DELETE can hold the file for a moment: a few short retries
        for (var attempt = 1; ; attempt++)
        {
            if (TryAtomicWrite(path, data, backup))
                return true;
            if (attempt >= WriteAttempts)
                return false;
            Thread.Sleep(20 * attempt);
        }
    }

    const int WriteAttempts = 5;

    static bool TryAtomicWrite(string path, byte[] data, bool backup)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return false;
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (new FileInfo(tmp).Length != data.Length)
                throw new IOException("short write");
            if (File.Exists(path))
                File.Replace(tmp, path, backup ? path + ".bak" : null, true);
            else
                File.Move(tmp, path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // File.Replace may fail after moving the target away (ERROR_UNABLE_TO_MOVE_REPLACEMENT): the new data is
            // the only copy left, so it takes the target's place instead of being deleted
            if (!File.Exists(path) && File.Exists(tmp))
            {
                try
                {
                    File.Move(tmp, path);
                    return true;
                }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
            TryDelete(tmp);
            return false;
        }
    }

    public static bool AtomicWriteText(string path, string text, bool backup = false) =>
        AtomicWrite(path, Utf8NoBom.GetBytes(text), backup);

    public static bool WriteJson(string path, JsonNode node, bool backup = false) =>
        AtomicWriteText(path, node.ToJsonString(Pretty) + "\n", backup);

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static long? Size(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string? Sha256File(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>true when <paramref name="path"/> is a normalized absolute path strictly inside <paramref name="root"/>
    /// (no "." or ".." segments, no empty segments).</summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            return false;
        var r = root.TrimEnd('\\', '/');
        if (path.Length <= r.Length + 1 || !path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            return false;
        var sep = path[r.Length];
        if (sep != '\\' && sep != '/')
            return false;
        foreach (var seg in path[(r.Length + 1)..].Split('\\', '/'))
            if (seg.Length == 0 || seg == "." || seg == "..")
                return false;
        return true;
    }
}
