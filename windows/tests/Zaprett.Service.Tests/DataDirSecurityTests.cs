using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

public class DataDirSecurityTests
{
    [Fact]
    public void PlainDirectories_Pass()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, Path.Combine(dir.Path, "data"));
        paths.EnsureDataDirs();
        DataDirSecurity.EnsureNoReparsePoints(paths);
    }

    [Fact]
    public void JunctionInsideDataDir_IsRefused()
    {
        using var dir = new TempDir();
        var paths = new WindowsPaths(dir.Path, Path.Combine(dir.Path, "data"));
        Directory.CreateDirectory(paths.DataDir);
        string target = Path.Combine(dir.Path, "elsewhere");
        Directory.CreateDirectory(target);
        // a junction needs no privileges: exactly what a plain user could plant
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/c", "mklink", "/J", paths.LogDir, target })
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
            p.WaitForExit();
        Assert.True(new DirectoryInfo(paths.LogDir).Attributes.HasFlag(FileAttributes.ReparsePoint), "junction was not created");
        var e = Assert.Throws<InvalidOperationException>(() => DataDirSecurity.EnsureNoReparsePoints(paths));
        Assert.Contains("logs", e.Message);
    }

    [Fact]
    public void Acl_IsProtected_AdminsOwn_UsersOnlyRead()
    {
        var sec = DataDirSecurity.BuildAcl();
        Assert.True(sec.AreAccessRulesProtected);
        Assert.Equal("S-1-5-32-544", sec.GetOwner(typeof(SecurityIdentifier))!.Value);
        var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        var users = rules.Single(r => r.IdentityReference.Value == "S-1-5-32-545");
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.AppendData));
        Assert.All(rules.Where(r => r.IdentityReference.Value != "S-1-5-32-545"),
            r => Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights));
    }
}
