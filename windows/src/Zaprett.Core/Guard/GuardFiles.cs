using Zaprett.Core.Platform;
using Zaprett.Core.Util;

namespace Zaprett.Core.Guard;

/// <summary>Guard files against empty lists (router ADR-004): a whitelist profile always has at least one entry that
/// never matches, so an empty include list cannot turn into "everything". Same content as
/// packages/zaprett/files/usr/share/zaprett/guard/*.txt; the core writes them to &lt;DataDir&gt;\guard when missing or
/// different, so they never depend on the installer.</summary>
public static class GuardFiles
{
    public const string HostlistContent = "zaprett-guard.invalid\n";
    public const string IpsetContent = "192.0.2.255/32\n100::ffff/128\n";

    public static string Dir(IPaths paths) => Path.Combine(paths.DataDir, "guard");

    public static string Hostlist(IPaths paths) => Path.Combine(Dir(paths), "hostlist-guard.txt");

    public static string Ipset(IPaths paths) => Path.Combine(Dir(paths), "ipset-guard.txt");

    /// <summary>Writes both files when missing or changed. Returns false when a file could not be written.</summary>
    public static bool Ensure(IPaths paths)
    {
        var ok = true;
        foreach (var (path, content) in new[] { (Hostlist(paths), HostlistContent), (Ipset(paths), IpsetContent) })
        {
            if (Files.ReadLimited(path, 4096) == content)
                continue;
            ok &= Files.AtomicWriteText(path, content);
        }
        return ok;
    }
}
