using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Jobs;
using Zaprett.Core.Platform;
using Zaprett.Core.Presets;
using Zaprett.Core.Store;
using Zaprett.Core.Strategy;
using Zaprett.Core.Text;
using Zaprett.Core.Util;

namespace Zaprett.Core;

/// <summary>Everything the parts of the core share: platform, options, configuration, item store, generator, jobs.</summary>
public sealed class CoreContext
{
    public CoreContext(PlatformServices platform, CoreOptions? options = null)
    {
        P = platform;
        Options = options ?? new CoreOptions();
        Config = new ConfigStore(platform.Paths.ConfigFile);
        Store = new ItemStore(platform.Paths);
        Generator = new ArgsGenerator(platform.Paths, Store);
        Jobs = new JobManager(platform.Paths, platform.Clock);
    }

    public PlatformServices P { get; }
    public CoreOptions Options { get; }
    public ConfigStore Config { get; }
    public ItemStore Store { get; }
    public ArgsGenerator Generator { get; }
    public JobManager Jobs { get; }

    public IPaths Paths => P.Paths;

    public long Now => P.Clock.Now.ToUnixTimeSeconds();

    public string RunFile(string name) => Path.Combine(P.Paths.RunDir, name);

    public JsonObject? LoadPresets() => PresetLogic.Load(P.Paths.PresetsFile);

    readonly Lock installIdGate = new();
    string? installId;

    /// <summary>&lt;DataDir&gt;\install-id: a random id created with the data directory. It survives service restarts and
    /// upgrades and disappears with the data (uninstall with REMOVEDATA), so the app can tell a new installation from an
    /// old one (its first-run wizard). An unreadable or invalid file is replaced by a new id.</summary>
    public string InstallId
    {
        get
        {
            lock (installIdGate)
            {
                if (installId != null)
                    return installId;
                var path = Path.Combine(P.Paths.DataDir, "install-id");
                var text = Files.ReadLimited(path, 256)?.Trim();
                if (Guid.TryParseExact(text, "D", out var g))
                    return installId = g.ToString("D");
                var id = Guid.NewGuid().ToString("D");
                if (!Files.AtomicWriteText(path, id + "\n"))
                    P.Log.Warn(T.S("log.install_id_write_failed", path));
                return installId = id;
            }
        }
    }

    /// <summary>Written by "stop", removed by "start": the user stopped the service on purpose, so the watchdog and the
    /// monitor leave it alone (router contract v1.3 §14.3).</summary>
    public string StoppedMarker => RunFile("stopped");

    public bool UserStopped => File.Exists(StoppedMarker);

    public void SetUserStopped(bool stopped)
    {
        if (stopped)
            Files.AtomicWriteText(StoppedMarker, Now + "\n");
        else
            Files.TryDelete(StoppedMarker);
    }
}
