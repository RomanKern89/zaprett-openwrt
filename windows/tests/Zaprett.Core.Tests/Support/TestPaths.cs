using Zaprett.Core.Platform;

namespace Zaprett.Core.Tests.Support;

/// <summary>A sandbox layout: ProgramData and the program directory in a temporary directory; the bundle is the real
/// one from packages/ (read-only) unless a test makes its own.</summary>
public sealed class TestPaths : IPaths, IDisposable
{
    public TestPaths(string? bundleDir = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "zaprett-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        InstallDir = Path.Combine(Root, "Program Files", "zaprett");
        BundleDir = bundleDir ?? RepoPaths.BundleDir;
        PresetsFile = Path.Combine(InstallDir, "presets.json");
        EngineDir = Path.Combine(InstallDir, "engine");
        Engine2Dir = Path.Combine(InstallDir, "engine2");
        DataDir = Path.Combine(Root, "ProgramData", "zaprett");
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(DataDir);
        File.Copy(RepoPaths.PresetsFile, PresetsFile);
    }

    public string Root { get; }
    public string InstallDir { get; }
    public string BundleDir { get; }
    public string PresetsFile { get; }
    public string EngineDir { get; }
    public string Engine2Dir { get; }
    public string DataDir { get; }
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string UserDir => Path.Combine(DataDir, "user");
    public string InstalledDir => Path.Combine(DataDir, "installed");
    public string RunDir => Path.Combine(DataDir, "run");
    public string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Creates fake engine executables (the dry-run is answered by the fake process runner).</summary>
    public void CreateEngines()
    {
        Directory.CreateDirectory(EngineDir);
        Directory.CreateDirectory(Engine2Dir);
        File.WriteAllText(Path.Combine(EngineDir, "winws.exe"), "fake");
        File.WriteAllText(Path.Combine(Engine2Dir, "winws2.exe"), "fake");
    }

    public void WriteUser(string file, string text)
    {
        Directory.CreateDirectory(UserDir);
        File.WriteAllText(Path.Combine(UserDir, file), text);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class RepoPaths
{
    static readonly Lazy<string> RootDir = new(() =>
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "windows", "Zaprett.slnx")))
            d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    });

    public static string Root => RootDir.Value;

    public static string ShareDir => Path.Combine(Root, "packages", "zaprett", "files", "usr", "share", "zaprett");

    public static string BundleDir => Path.Combine(ShareDir, "bundle");

    public static string PresetsFile => Path.Combine(ShareDir, "presets.json");

    public static string GuardDir => Path.Combine(ShareDir, "guard");

    public static string ConformanceDir => Path.Combine(Root, "windows", "tests", "conformance");
}
