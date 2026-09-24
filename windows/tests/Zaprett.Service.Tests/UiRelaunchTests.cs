using System.ComponentModel;
using System.Text;
using Zaprett.Service.Hosting;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

/// <summary>D2: the tray the MSI closed comes back in the listed sessions that are still active, and only our exe.</summary>
public class UiRelaunchTests
{
    private sealed class FakeSessions : IUserSessions
    {
        public HashSet<int> Active { get; init; } = [];
        public HashSet<int> Running { get; init; } = [];
        public HashSet<int> Failing { get; init; } = [];
        public List<(int Session, string Exe, string Args, string Dir)> Launches { get; } = [];
        public int Queries;

        public IReadOnlySet<int> ActiveSessions()
        {
            Queries++;
            return Active;
        }

        /// <summary>Launches whose process is killed at once (the old MSI's taskkill /IM zaprett-ui.exe).</summary>
        public int KillFirst { get; set; }
        /// <summary>The installer state at each launch.</summary>
        public List<bool> InstallerBusyAtLaunch { get; } = [];
        public FakeInstaller? Installer { get; set; }

        public IReadOnlySet<int> SessionsRunning(string exePath)
        {
            lock (Running)
                return new HashSet<int>(Running);
        }

        public int Launch(int sessionId, string exePath, string arguments, string workingDir)
        {
            if (Failing.Contains(sessionId))
                throw new Win32Exception(5);
            Launches.Add((sessionId, exePath, arguments, workingDir));
            InstallerBusyAtLaunch.Add(Installer?.Busy ?? false);
            if (KillFirst > 0)
                KillFirst--;
            else
                lock (Running)
                    Running.Add(sessionId);
            return 1000 + sessionId;
        }
    }

    private sealed class FakeInstaller : IInstallerActivity
    {
        public volatile bool Busy;
        public int Checks;

        public bool IsRunning()
        {
            Interlocked.Increment(ref Checks);
            return Busy;
        }
    }

    private static readonly UiRelaunchTiming Fast = new(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50));

    private sealed class Rig : IDisposable
    {
        public TempDir Install { get; } = new();
        public TempDir Data { get; } = new();
        public WindowsPaths Paths { get; }
        public MemoryLog Log { get; } = new();
        public FakeSessions Sessions { get; }
        public UiRelaunch Relaunch { get; }
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;

        public Rig(FakeSessions sessions, bool uiExe = true, FakeInstaller? installer = null, UiRelaunchTiming? timing = null)
        {
            Paths = new WindowsPaths(Install.Path, Data.Path);
            Paths.EnsureDataDirs();
            if (uiExe)
                File.WriteAllText(Path.Combine(Install.Path, UiRelaunch.UiExeName), "exe");
            Sessions = sessions;
            sessions.Installer = installer;
            Relaunch = new UiRelaunch(Paths, sessions, Log, () => Now, installer, timing ?? Fast);
        }

        public void Write(string json) => File.WriteAllText(Relaunch.FilePath, json, new UTF8Encoding(false));

        public void Dispose()
        {
            Install.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void File_StartsTheTrayInTheActiveListedSessions_AndIsDeleted()
    {
        using var rig = new Rig(new FakeSessions { Active = [1, 3, 7] });
        rig.Write("""{"version":1,"sessions":[1,2,3]}""");
        var started = rig.Relaunch.Run();
        Assert.Equal([1, 3], started);
        Assert.Equal([1, 3], rig.Sessions.Launches.Select(l => l.Session));
        string exe = Path.Combine(rig.Paths.InstallDir, "zaprett-ui.exe");
        Assert.All(rig.Sessions.Launches, l =>
        {
            Assert.Equal(exe, l.Exe);
            Assert.Equal("--tray", l.Args);
            Assert.Equal(rig.Paths.InstallDir, l.Dir);
        });
        Assert.Contains(rig.Log.Lines, l => l.Contains("session 2 is not active"));
        Assert.False(File.Exists(rig.Relaunch.FilePath));
    }

    [Fact]
    public void NoFile_NothingHappens()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] });
        Assert.Empty(rig.Relaunch.Run());
        Assert.Empty(rig.Sessions.Launches);
        Assert.Equal(0, rig.Sessions.Queries);
        Assert.Empty(rig.Log.Lines);
    }

    [Fact]
    public void RunsOnce_TheSecondStartFindsNoFile()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] });
        rig.Write("""{"version":1,"sessions":[1]}""");
        Assert.Single(rig.Relaunch.Run());
        Assert.Empty(rig.Relaunch.Run());
        Assert.Single(rig.Sessions.Launches);
    }

    // security: whatever the file says, only zaprett-ui.exe of this installation is started
    [Fact]
    public void PathsInTheFile_AreIgnored()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] });
        rig.Write("""{"version":1,"sessions":[1],"exe":"C:\\Windows\\System32\\cmd.exe","args":"/c calc","dir":"C:\\"}""");
        rig.Relaunch.Run();
        var l = Assert.Single(rig.Sessions.Launches);
        Assert.Equal(Path.Combine(rig.Paths.InstallDir, "zaprett-ui.exe"), l.Exe);
        Assert.Equal("--tray", l.Args);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"sessions":[1]}""")]
    [InlineData("""{"version":2,"sessions":[1]}""")]
    [InlineData("""{"version":1,"sessions":"1"}""")]
    [InlineData("""{"version":1,"sessions":[1,"2"]}""")]
    [InlineData("""{"version":1,"sessions":[1.5]}""")]
    [InlineData("""[1,2]""")]
    public void InvalidFile_NoLaunch_ButDeleted(string json)
    {
        using var rig = new Rig(new FakeSessions { Active = [1, 2] });
        rig.Write(json);
        Assert.Empty(rig.Relaunch.Run());
        Assert.Empty(rig.Sessions.Launches);
        Assert.False(File.Exists(rig.Relaunch.FilePath));
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN ", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionZero_Negative_AndRepeats_AreDropped()
    {
        using var rig = new Rig(new FakeSessions { Active = [0, 1, 2] });
        rig.Write("""{"version":1,"sessions":[0,-1,2,2,1]}""");
        Assert.Equal([2, 1], rig.Relaunch.Run());
    }

    [Fact]
    public void MoreThan64Sessions_AreCut()
    {
        var ids = Enumerable.Range(1, 100).ToArray();
        using var rig = new Rig(new FakeSessions { Active = [.. ids] });
        rig.Write("""{"version":1,"sessions":[""" + string.Join(",", ids) + "]}");
        Assert.Equal(UiRelaunch.MaxSessions, rig.Relaunch.Run().Count);
    }

    [Fact]
    public void StaleFile_NoLaunch_ButDeleted()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] });
        rig.Write("""{"version":1,"sessions":[1]}""");
        rig.Now = DateTimeOffset.Now + UiRelaunch.MaxAge + TimeSpan.FromMinutes(1);
        Assert.Empty(rig.Relaunch.Run());
        Assert.Empty(rig.Sessions.Launches);
        Assert.False(File.Exists(rig.Relaunch.FilePath));
    }

    [Fact]
    public void FreshFile_JustInsideTheAge_Launches()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] });
        rig.Write("""{"version":1,"sessions":[1]}""");
        rig.Now = DateTimeOffset.Now + UiRelaunch.MaxAge - TimeSpan.FromMinutes(1);
        Assert.Single(rig.Relaunch.Run());
    }

    [Fact]
    public void LargeFile_NoLaunch_ButDeleted()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] });
        rig.Write("""{"version":1,"sessions":[1],"pad":" """ + new string('x', UiRelaunch.MaxBytes) + "\"}");
        Assert.Empty(rig.Relaunch.Run());
        Assert.False(File.Exists(rig.Relaunch.FilePath));
    }

    [Fact]
    public void TrayAlreadyRunning_InASession_IsNotStartedTwice()
    {
        using var rig = new Rig(new FakeSessions { Active = [1, 2], Running = [1] });
        rig.Write("""{"version":1,"sessions":[1,2]}""");
        Assert.Equal([2], rig.Relaunch.Run());
    }

    [Fact]
    public void FailedLaunch_InOneSession_DoesNotStopTheOthers()
    {
        using var rig = new Rig(new FakeSessions { Active = [1, 2], Failing = [1] });
        rig.Write("""{"version":1,"sessions":[1,2]}""");
        Assert.Equal([2], rig.Relaunch.Run());
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN tray relaunch: session 1", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingUiExe_NoLaunch_ButDeleted()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] }, uiExe: false);
        rig.Write("""{"version":1,"sessions":[1]}""");
        Assert.Empty(rig.Relaunch.Run());
        Assert.Empty(rig.Sessions.Launches);
        Assert.False(File.Exists(rig.Relaunch.FilePath));
    }

    [Fact]
    public void Utf8Bom_IsAccepted()
    {
        using var rig = new Rig(new FakeSessions { Active = [1] });
        File.WriteAllText(rig.Relaunch.FilePath, """{"version":1,"sessions":[1]}""", new UTF8Encoding(true));
        Assert.Single(rig.Relaunch.Run());
    }

    // D16: while the installer works (the old product's removal kills every zaprett-ui.exe) the tray is not started;
    // it starts once the installer has finished, and the file stays until then
    [Fact]
    public async Task InstallerBusy_StartWaits_ThenStarts()
    {
        var installer = new FakeInstaller { Busy = true };
        using var rig = new Rig(new FakeSessions { Active = [1] }, installer: installer);
        rig.Write("""{"version":1,"sessions":[1]}""");
        var run = rig.Relaunch.RunAsync(default);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref installer.Checks) >= 3, TimeSpan.FromSeconds(5)));
        Assert.Empty(rig.Sessions.Launches);
        Assert.True(File.Exists(rig.Relaunch.FilePath), "the file must stay while waiting");
        installer.Busy = false;
        Assert.Equal([1], await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(rig.Sessions.Launches);
        Assert.Equal([false], rig.Sessions.InstallerBusyAtLaunch);
        Assert.False(File.Exists(rig.Relaunch.FilePath));
        Assert.Contains(rig.Log.Lines, l => l.Contains("Windows Installer is still working"));
    }

    // negative control: an installer that does not finish within the limit: no start, a WARN, the file is removed
    [Fact]
    public async Task InstallerNeverFinishes_NoStart_AfterTheLimit()
    {
        var installer = new FakeInstaller { Busy = true };
        using var rig = new Rig(new FakeSessions { Active = [1] }, installer: installer,
            timing: new UiRelaunchTiming(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50)));
        rig.Write("""{"version":1,"sessions":[1]}""");
        // the limit counts polls, not the clock; the outer bound only guards against a hang (a loaded thread pool in a
        // full parallel test run made 5 s too short)
        Assert.Empty(await rig.Relaunch.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(rig.Sessions.Launches);
        Assert.False(File.Exists(rig.Relaunch.FilePath));
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN tray relaunch: Windows Installer still works", StringComparison.Ordinal));
    }

    // the service stops while waiting (the installer restarts it): the file stays for the next start
    [Fact]
    public async Task StopWhileWaiting_KeepsTheFile()
    {
        var installer = new FakeInstaller { Busy = true };
        using var rig = new Rig(new FakeSessions { Active = [1] }, installer: installer);
        rig.Write("""{"version":1,"sessions":[1]}""");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Relaunch.RunAsync(cts.Token));
        Assert.True(File.Exists(rig.Relaunch.FilePath));
        Assert.Empty(rig.Sessions.Launches);
    }

    // the tray killed right after the start (taskkill of the old product) is started once more
    [Fact]
    public void KilledRightAfterTheStart_StartedOnceMore()
    {
        using var rig = new Rig(new FakeSessions { Active = [1, 2], KillFirst = 1 });
        rig.Write("""{"version":1,"sessions":[1,2]}""");
        Assert.Equal([1, 2], rig.Relaunch.Run().Order());
        Assert.Equal([1, 2, 1], rig.Sessions.Launches.Select(l => l.Session));
        Assert.Contains(rig.Log.Lines, l => l.Contains("the tray ended in session(s) 1 right after the start, starting it once more"));
    }

    // negative control: a tray that lives is not started twice, and only one more start is tried
    [Fact]
    public void Survives_NoSecondStart_AndOnlyOneRetry()
    {
        using (var rig = new Rig(new FakeSessions { Active = [1] }))
        {
            rig.Write("""{"version":1,"sessions":[1]}""");
            rig.Relaunch.Run();
            Assert.Single(rig.Sessions.Launches);
            Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("once more"));
        }
        using (var rig = new Rig(new FakeSessions { Active = [1], KillFirst = 5 }))
        {
            rig.Write("""{"version":1,"sessions":[1]}""");
            rig.Relaunch.Run();
            // one start and one more, never a loop
            Assert.Equal(2, rig.Sessions.Launches.Count);
        }
    }

    // the real installer check on this workstation, read-only: no installation runs here
    [Fact]
    [Trait("Category", "System")]
    public void WindowsInstallerMutex_IsFreeHere()
    {
        Assert.False(new WindowsInstallerMutex(new MemoryLog()).IsRunning());
    }

    // the real WTS side on this workstation, read-only: the current session is active
    [Fact]
    [Trait("Category", "System")]
    public void Wts_CurrentSessionIsActive()
    {
        var wts = new Zaprett.Service.Native.WtsUserSessions(new MemoryLog());
        Assert.Contains(System.Diagnostics.Process.GetCurrentProcess().SessionId, wts.ActiveSessions());
    }
}
