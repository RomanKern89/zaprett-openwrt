using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>A WMI provider busy for a while after an install: Info and the last reading, one WARN only after a long
/// failure, a line when it works again.</summary>
public class TransientReadTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 23, 23, 5, 0, TimeSpan.Zero);
    }

    private static bool Fails(Exception e) => e is InvalidOperationException;

    private static (TransientRead<string> Read, MemoryLog Log, Clock Clock) Make()
    {
        var log = new MemoryLog();
        var clock = new Clock();
        return (new TransientRead<string>("systeminfo: MSFT_MpComputerStatus", log, () => clock.Now, TimeSpan.Zero), log, clock);
    }

    [Fact]
    public void FirstAttemptFails_SecondWorks_NothingLogged()
    {
        var (r, log, _) = Make();
        int calls = 0;
        Assert.Equal("ok", r.Get(() => ++calls == 1 ? throw new InvalidOperationException("Generic failure") : "ok", Fails));
        Assert.Equal(2, calls);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void ShortFailure_IsInfo_WithTheLastReading()
    {
        var (r, log, clock) = Make();
        Assert.Equal("on", r.Get(() => "on", Fails));
        for (int i = 0; i < 3; i++)
        {
            clock.Now += TimeSpan.FromSeconds(5);
            Assert.Equal("on", r.Get(() => throw new InvalidOperationException("Provider load failure"), Fails));
        }
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal));
        Assert.Equal(3, log.Lines.Count(l => l.StartsWith("INFO systeminfo: MSFT_MpComputerStatus: Provider load failure", StringComparison.Ordinal)));
        clock.Now += TimeSpan.FromSeconds(5);
        Assert.Equal("off", r.Get(() => "off", Fails));
        Assert.Contains(log.Lines, l => l == "INFO systeminfo: MSFT_MpComputerStatus: readable again");
    }

    // negative control: a failure that lasts is a WARN, once
    [Fact]
    public void LongFailure_IsOneWarn()
    {
        var (r, log, clock) = Make();
        for (int i = 0; i < 6; i++)
        {
            Assert.Null(r.Get(() => throw new InvalidOperationException("Generic failure"), Fails));
            clock.Now += TimeSpan.FromMinutes(1);
        }
        var warn = Assert.Single(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal));
        Assert.StartsWith("WARN systeminfo: MSFT_MpComputerStatus: fails for 2 min: Generic failure", warn, StringComparison.Ordinal);
    }

    // a success in between starts the count again
    [Fact]
    public void SuccessInBetween_ResetsTheClock()
    {
        var (r, log, clock) = Make();
        r.Get(() => throw new InvalidOperationException("x"), Fails);
        clock.Now += TimeSpan.FromSeconds(90);
        r.Get(() => "ok", Fails);
        clock.Now += TimeSpan.FromSeconds(90);
        r.Get(() => throw new InvalidOperationException("x"), Fails);
        clock.Now += TimeSpan.FromSeconds(90);
        r.Get(() => throw new InvalidOperationException("x"), Fails);
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal));
    }

    // an unexpected exception is not hidden
    [Fact]
    public void OtherExceptions_Propagate()
    {
        var (r, _, _) = Make();
        Assert.Throws<ArgumentException>(() => r.Get(() => throw new ArgumentException("bug"), Fails));
    }
}
