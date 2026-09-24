using System.Text;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// The engine's debug output (winws --debug=1 prints it on stdout) in run\engine-debug.log. winws itself can write
/// it to a file (--debug=@file), but it opens and closes the file for every line: with debugging on each handled
/// connection took ~1.5 s longer (the Windows 10 test machine, 2026-09-23: TLS 1.5-2.1 s against 0.4 s with --debug=1) and the first request
/// after a restart could be dropped. Here the lines go through one open file with a buffer, written out every
/// <see cref="FlushInterval"/> and when the engine stops. Each start moves the previous file to .1, and so does
/// reaching <see cref="MaxBytes"/>. UTF-8 without BOM, LF, as winws wrote it; readers may open it while it is written.
/// </summary>
public sealed class EngineDebugLog : IDisposable
{
    public const long DefaultMaxBytes = 16L * 1024 * 1024;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string _path;
    private readonly ILog? _log;
    private readonly long _maxBytes;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private StreamWriter? _writer;
    private long _bytes;
    private bool _disposed;

    public EngineDebugLog(string path, ILog? log = null, long maxBytes = DefaultMaxBytes)
    {
        _path = path;
        _log = log;
        _maxBytes = maxBytes;
        lock (_lock)
            Open();
        _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public string Path => _path;

    /// <summary>File name of an instance's debug log: engine-debug.log for main, engine-debug-&lt;instance&gt;.log else.</summary>
    public static string FileFor(string runDir, string instance) =>
        System.IO.Path.Combine(runDir, instance == EngineControl.MainInstance ? "engine-debug.log" : $"engine-debug-{instance}.log");

    /// <summary>winws prints its debug output on stdout with --debug=1 (a bare --debug means the same).</summary>
    public static bool IsConsoleDebug(IEnumerable<string> args) => args.Any(a => a is "--debug" or "--debug=1");

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_disposed || _writer is null)
                return;
            _writer.Write(line);
            _writer.Write('\n');
            _bytes += Utf8.GetByteCount(line) + 1;
            if (_bytes >= _maxBytes)
            {
                Close();
                Open();
            }
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Flush();
            }
            catch (IOException)
            {
                // a full disk must not stop the engine's output pump
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Close();
        }
    }

    // caller holds _lock
    private void Open()
    {
        try
        {
            if (File.Exists(_path))
                File.Move(_path, _path + ".1", overwrite: true);
            var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(fs, Utf8, 64 * 1024);
            _bytes = 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _writer = null;
            _log?.Warn($"engine debug log {_path}: {e.Message}");
        }
    }

    // caller holds _lock
    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }
        _writer = null;
    }
}
