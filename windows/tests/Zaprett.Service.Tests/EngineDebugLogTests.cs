using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>winws --debug=1 prints the debug output on stdout; the service keeps it in run\engine-debug.log (one open
/// file, buffered) and the journal only gets the ready/waiting lines. Without --debug=1 everything stays in the journal.</summary>
public class EngineDebugLogTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    // "& rem" swallows the arguments after the script, as winws would read --debug=1
    private static string[] Engine(string debugArg) =>
        ["/c", "echo loading list& echo windivert initialized. capture is started.& echo packet: sending fake& ping -n 30 127.0.0.1 >nul& rem", debugArg];

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    [Fact]
    public async Task ConsoleDebug_GoesToTheFile_JournalKeepsOnlyTheReadyLine()
    {
        using var dir = new TempDir();
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(), EngineRestartPolicy.Winws with { RestartDelay = TimeSpan.FromSeconds(30) },
            runDir: dir.Path);
        var st = await engine.StartAsync("main", Cmd, Engine("--debug=1"), default);
        Assert.True(st.Running);
        string file = Path.Combine(dir.Path, "engine-debug.log");
        // written out by the timer while the engine still runs
        Assert.True(await Wait.UntilAsync(() => File.Exists(file) && ReadShared(file).Contains("packet: sending fake"), TimeSpan.FromSeconds(5)),
            "debug lines did not reach the file");
        string text = ReadShared(file);
        Assert.Contains("loading list\n", text);
        Assert.Contains("windivert initialized. capture is started.\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.Contains(log.Lines, l => l.Contains("engine main [out]: windivert initialized"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("[out]: packet: sending fake"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("[out]: loading list"));
        Assert.Contains(log.Lines, l => l.Contains("debug output goes to " + file));
    }

    // negative control: without --debug=1 there is no file and the output stays in the journal as before
    [Fact]
    public async Task WithoutConsoleDebug_NoFile_OutputInTheJournal()
    {
        using var dir = new TempDir();
        var log = new MemoryLog();
        await using var engine = new EngineControl(log, new SystemClock(), EngineRestartPolicy.Winws with { RestartDelay = TimeSpan.FromSeconds(30) },
            runDir: dir.Path);
        await engine.StartAsync("main", Cmd, Engine("--debug=@" + Path.Combine(dir.Path, "x.log")), default);
        Assert.True(await Wait.UntilAsync(() => log.Lines.Any(l => l.Contains("[out]: packet: sending fake")), TimeSpan.FromSeconds(5)));
        Assert.Contains(log.Lines, l => l.Contains("[out]: loading list"));
        Assert.False(File.Exists(Path.Combine(dir.Path, "engine-debug.log")));
    }

    [Fact]
    public async Task EachStart_MovesThePreviousRunTo1_AndStopWritesTheRest()
    {
        using var dir = new TempDir();
        await using var engine = new EngineControl(new MemoryLog(), new SystemClock(), EngineRestartPolicy.Winws with { RestartDelay = TimeSpan.FromSeconds(30) },
            runDir: dir.Path);
        string file = Path.Combine(dir.Path, "engine-debug.log");
        await engine.StartAsync("main", Cmd, ["/c", "echo first run& echo windivert initialized& ping -n 30 127.0.0.1 >nul& rem", "--debug=1"], default);
        await engine.StopAsync("main", default);
        // stopped before the timer (1 s): the stop itself wrote the buffer out
        Assert.Contains("first run", ReadShared(file));
        await engine.StartAsync("main", Cmd, ["/c", "echo second run& echo windivert initialized& ping -n 30 127.0.0.1 >nul& rem", "--debug=1"], default);
        Assert.True(await Wait.UntilAsync(() => ReadShared(file).Contains("second run"), TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("first run", ReadShared(file));
        Assert.Contains("first run", ReadShared(file + ".1"));
    }

    [Fact]
    public async Task TestInstance_HasItsOwnFile()
    {
        using var dir = new TempDir();
        await using var engine = new EngineControl(new MemoryLog(), new SystemClock(), EngineRestartPolicy.Winws with { RestartDelay = TimeSpan.FromSeconds(30) },
            runDir: dir.Path);
        await engine.StartAsync("test", Cmd, ["/c", "echo candidate& echo windivert initialized& ping -n 30 127.0.0.1 >nul& rem", "--debug=1"], default);
        string file = Path.Combine(dir.Path, "engine-debug-test.log");
        Assert.True(await Wait.UntilAsync(() => File.Exists(file) && ReadShared(file).Contains("candidate"), TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(Path.Combine(dir.Path, "engine-debug.log")));
    }

    [Fact]
    public void SizeLimit_Rotates()
    {
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "engine-debug.log");
        using (var d = new EngineDebugLog(file, maxBytes: 100))
        {
            for (int i = 0; i < 30; i++)
                d.WriteLine($"line {i:00}");   // 8 bytes each with LF
        }
        Assert.True(new FileInfo(file).Length < 100);
        Assert.True(File.Exists(file + ".1"));
        Assert.Contains("line 29\n", File.ReadAllText(file));
    }

    [Fact]
    public void Dispose_WritesTheBuffer_AndLaterLinesAreDropped()
    {
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "engine-debug.log");
        var d = new EngineDebugLog(file);
        d.WriteLine("before");
        d.Dispose();
        d.WriteLine("after");
        Assert.Equal("before\n", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("--debug=1", true)]
    [InlineData("--debug", true)]
    [InlineData("--debug=0", false)]
    [InlineData("--debug=@C:\\x.log", false)]
    [InlineData("--debug=syslog", false)]
    public void ConsoleDebugFlag(string arg, bool expected) =>
        Assert.Equal(expected, EngineDebugLog.IsConsoleDebug(["--wf-l3=ipv4", arg]));
}
