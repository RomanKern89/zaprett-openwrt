using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

public class FileLogTests
{
    [Fact]
    public void Rotation_KeepsFiveFilesOfBoundedSize()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path, maxFileBytes: 1024, files: 5);
        for (int i = 0; i < 400; i++)
            log.Info($"line {i:D4} " + new string('x', 40));
        var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["zaprett.log", "zaprett.log.1", "zaprett.log.2", "zaprett.log.3", "zaprett.log.4"], files);
        Assert.All(Directory.GetFiles(dir.Path), f => Assert.InRange(new FileInfo(f).Length, 1, 1024));
        // the newest line is in zaprett.log, older ones moved down the chain
        Assert.Contains("line 0399", File.ReadAllText(Path.Combine(dir.Path, "zaprett.log")));
        Assert.DoesNotContain("line 0000", string.Concat(Directory.GetFiles(dir.Path).Select(File.ReadAllText)));
    }

    [Fact]
    public void Tail_SpansRotatedFiles_InOrder()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path, maxFileBytes: 512, files: 5);
        for (int i = 0; i < 40; i++)
            log.Info($"n{i:D3} " + new string('y', 30));
        var tail = log.Tail(20);
        Assert.Equal(20, tail.Count);
        Assert.EndsWith("n039 " + new string('y', 30), tail[^1]);
        var numbers = tail.Select(l => int.Parse(System.Text.RegularExpressions.Regex.Match(l, @" n(\d{3}) ").Groups[1].Value)).ToList();
        Assert.Equal(Enumerable.Range(20, 20), numbers);
    }

    [Fact]
    public void Tail_IsClampedAndWorksOnEmptyLog()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path);
        Assert.Empty(log.Tail(10));
        log.Warn("one");
        Assert.Single(log.Tail(0));
        Assert.Single(log.Tail(100000));
    }

    // Deterministic: the reader holds the file for the whole write (no timing), the line waits in memory and is
    // written, in order, with the next line or by Tail.
    [Fact]
    public void ReaderWithoutWriteSharing_DoesNotLoseLines()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path);
        log.Info("first");
        string file = Path.Combine(dir.Path, FileLog.FileName);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))   // like File.ReadAllText
        {
            log.Info("second");
            log.Info("third");
            Assert.Equal(2, log.PendingLines);
        }
        log.Info("fourth");
        Assert.Equal(0, log.PendingLines);
        var lines = File.ReadAllLines(file).Select(l => l[(l.LastIndexOf(' ') + 1)..]).ToList();
        Assert.Equal(["first", "second", "third", "fourth"], lines);
    }

    [Fact]
    public void PendingLines_AreFlushedByTail()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path);
        log.Info("first");
        string file = Path.Combine(dir.Path, FileLog.FileName);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            log.Info("second");
        Assert.EndsWith("second", log.Tail(10)[^1]);
        Assert.Equal(0, log.PendingLines);
    }

    [Fact]
    public void Backlog_IsBounded_WhenTheFileStaysBusy()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path, maxFileBytes: 1024);
        log.Info("first");
        using (new FileStream(Path.Combine(dir.Path, FileLog.FileName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (int i = 0; i < 200; i++)
                log.Info("busy " + new string('z', 40));
            Assert.InRange(log.PendingLines, 1, 1024 / 60 + 1);
        }
    }

    [Fact]
    public void Messages_AreOneLine_AndUrlQueriesMasked()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path);
        log.Error("bad\r\ninjected line\u0007 https://example.com/list.txt?token=SECRET&x=1 end");
        var lines = File.ReadAllLines(Path.Combine(dir.Path, "zaprett.log"));
        Assert.Single(lines);
        Assert.DoesNotContain("SECRET", lines[0]);
        Assert.Contains("https://example.com/list.txt?***", lines[0]);
        Assert.Contains(" ERROR bad  injected line  ", lines[0]);
    }
}
