using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// A system reading that can fail for a while and then works again: right after an install the WMI provider of
/// Defender answered "Generic failure" / "Provider load failure" for ~10 s (the Windows 10 test machine, 2026-09-23), three WARN lines in
/// the journal for nothing. A failed read is tried once more after <see cref="DefaultRetryDelay"/>; if it still fails,
/// the last good value is returned (or the default). Such failures are Info; one WARN comes only when the reading has
/// failed for <see cref="DefaultWarnAfter"/> without a break, and an Info line when it works again.
/// </summary>
public sealed class TransientRead<T>(string what, ILog log, Func<DateTimeOffset>? now = null, TimeSpan? retryDelay = null,
    TimeSpan? warnAfter = null)
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultWarnAfter = TimeSpan.FromMinutes(2);

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private readonly TimeSpan _retryDelay = retryDelay ?? DefaultRetryDelay;
    private readonly TimeSpan _warnAfter = warnAfter ?? DefaultWarnAfter;
    private T? _last;
    private DateTimeOffset? _failingSince;
    private bool _warned;

    /// <summary>The reading, or after two failed attempts the last good one; <paramref name="isFailure"/> tells the
    /// expected failures (others propagate).</summary>
    public T? Get(Func<T?> read, Func<Exception, bool> isFailure)
    {
        Exception? error = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var value = read();
                Succeeded(value);
                return value;
            }
            catch (Exception e) when (isFailure(e))
            {
                error = e;
                if (attempt == 0 && _retryDelay > TimeSpan.Zero)
                    Thread.Sleep(_retryDelay);
            }
        }
        return Failed(error!.Message);
    }

    private void Succeeded(T? value)
    {
        lock (_lock)
        {
            _last = value;
            if (_failingSince is not null)
                log.Info($"{what}: readable again");
            _failingSince = null;
            _warned = false;
        }
    }

    private T? Failed(string message)
    {
        lock (_lock)
        {
            var t = _now();
            _failingSince ??= t;
            var failing = t - _failingSince.Value;
            if (failing >= _warnAfter && !_warned)
            {
                _warned = true;
                log.Warn($"{what}: fails for {failing.TotalMinutes:0} min: {message}");
            }
            else
            {
                log.Info($"{what}: {message} (for now; the last reading is used)");
            }
            return _last;
        }
    }
}
