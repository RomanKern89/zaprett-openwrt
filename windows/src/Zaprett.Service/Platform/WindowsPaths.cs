using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>Layout of ARCHITECTURE-WIN §3. For development ZAPRETT_INSTALL_DIR and ZAPRETT_DATA_DIR override
/// the Program Files and ProgramData roots.</summary>
public sealed class WindowsPaths : IPaths
{
    public const string InstallDirVariable = "ZAPRETT_INSTALL_DIR";
    public const string DataDirVariable = "ZAPRETT_DATA_DIR";

    public WindowsPaths(string installDir, string dataDir)
    {
        InstallDir = Path.GetFullPath(installDir);
        DataDir = Path.GetFullPath(dataDir);
    }

    public static WindowsPaths FromEnvironment()
    {
        // the install folder is chosen in the MSI (S-2) and recorded in HKLM\SOFTWARE\zaprett\InstallDir
        string install = Environment.GetEnvironmentVariable(InstallDirVariable) is { Length: > 0 } i
            ? i
            : RegistryInstallDir() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "zaprett");
        string data = Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "zaprett");
        return new WindowsPaths(install, data);
    }

    private static string? RegistryInstallDir()
    {
        try
        {
            return Hosting.InstallOptions.Read()?.InstallDir is { Length: > 0 } dir && Path.IsPathFullyQualified(dir) ? dir : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public string InstallDir { get; }
    public string BundleDir => Path.Combine(InstallDir, "bundle");
    public string PresetsFile => Path.Combine(InstallDir, "presets.json");
    public string EngineDir => Path.Combine(InstallDir, "engine");
    public string Engine2Dir => Path.Combine(InstallDir, "engine2");
    public string DataDir { get; }
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string UserDir => Path.Combine(DataDir, "user");
    public string InstalledDir => Path.Combine(DataDir, "installed");
    public string RunDir => Path.Combine(DataDir, "run");
    public string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Creates the data directories the service writes to.</summary>
    public void EnsureDataDirs()
    {
        foreach (var d in new[] { DataDir, UserDir, InstalledDir, RunDir, LogDir })
            Directory.CreateDirectory(d);
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
