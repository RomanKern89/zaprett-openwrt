using System.Text;
using System.Text.RegularExpressions;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// Service and engine journal: logs\zaprett.log, rotated at <see cref="MaxFileBytes"/> into zaprett.log.1 … .4
/// (5 files of 1 MiB, ARCHITECTURE-WIN §3). One line per message; line breaks are flattened and URL queries masked
/// (they may carry subscription tokens, W-8).
/// </summary>
public sealed partial class FileLog : ILog
{
    public const string FileName = "zaprett.log";
    public const int MaxTailLines = 1000;

    private readonly object _lock = new();
    private readonly string _path;
    private readonly Action<string>? _mirror;
    private readonly Func<DateTimeOffset> _now;

    public long MaxFileBytes { get; }
    public int Files { get; }

    public FileLog(string logDir, long maxFileBytes = 1024 * 1024, int files = 5, Action<string>? mirror = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(files, 1);
        Directory.CreateDirectory(logDir);
        _path = Path.Combine(logDir, FileName);
        MaxFileBytes = maxFileBytes;
        Files = files;
        _mirror = mirror;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    [GeneratedRegex(@"(https?://[^\s?#]+)\?[^\s#]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"[\x00-\x08\x0A-\x1F\x7F]")]
    private static partial Regex ControlChars();

    public static string Sanitize(string message) =>
        UrlQuery().Replace(ControlChars().Replace(message ?? "", " "), "$1?***");

    private void Write(string level, string message)
    {
        string line = $"{_now():yyyy-MM-dd HH:mm:ss.fff} {level} {Sanitize(message)}";
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_lock)
        {
            _pending.Add(bytes);
            _pendingBytes += bytes.Length;
            FlushPending();
        }
        _mirror?.Invoke(line);
    }

    // lines not written yet: a reader without FileShare.Write (a viewer, a test) can hold the file for any time
    private readonly List<byte[]> _pending = [];
    private long _pendingBytes;

    /// <summary>Lines kept in memory because the file was busy; written before the next line.</summary>
    public int PendingLines
    {
        get
        {
            lock (_lock)
                return _pending.Count;
        }
    }

    /// <summary>Writes the pending lines in order. A busy file gets two short retries, then the lines wait for the
    /// next write — never a lost line and never a long stall of the logging thread. The backlog is capped at one file
    /// size (oldest lines dropped) so a file held open forever cannot grow memory without bound.</summary>
    private void FlushPending()
    {
        for (int attempt = 0; attempt < 3 && _pending.Count > 0; attempt++)
        {
            try
            {
                foreach (var bytes in _pending)
                {
                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > 0 && fi.Length + bytes.Length > MaxFileBytes)
                        Rotate();
                    using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fs.Write(bytes);
                    _pendingBytes -= bytes.Length;
                    // written lines leave the queue one by one: a failure in the middle keeps only the rest
                    _written++;
                }
                _pending.Clear();
                _written = 0;
                return;
            }
            catch (IOException)
            {
                DropWritten();
                if (attempt < 2)
                    Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException)
            {
                DropWritten();
                break;
            }
        }
        while (_pendingBytes > MaxFileBytes && _pending.Count > 1)
        {
            _pendingBytes -= _pending[0].Length;
            _pending.RemoveAt(0);
        }
    }

    private int _written;

    private void DropWritten()
    {
        _pending.RemoveRange(0, _written);
        _written = 0;
    }

    private void Rotate()
    {
        string Name(int i) => i == 0 ? _path : _path + "." + i;
        File.Delete(Name(Files - 1));
        for (int i = Files - 2; i >= 0; i--)
            if (File.Exists(Name(i)))
                File.Move(Name(i), Name(i + 1), overwrite: true);
    }

    public IReadOnlyList<string> Tail(int lines)
    {
        lines = Math.Clamp(lines, 1, MaxTailLines);
        var result = new List<string>();
        lock (_lock)
        {
            FlushPending();
            // newest file first; stop as soon as enough lines are collected
            for (int i = 0; i < Files && result.Count < lines; i++)
            {
                string p = i == 0 ? _path : _path + "." + i;
                if (!File.Exists(p))
                    break;
                string[] fileLines;
                try
                {
                    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    fileLines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                }
                catch (IOException)
                {
                    break;
                }
                int take = Math.Min(lines - result.Count, fileLines.Length);
                result.InsertRange(0, fileLines[^take..]);
            }
        }
        return result;
    }
}
