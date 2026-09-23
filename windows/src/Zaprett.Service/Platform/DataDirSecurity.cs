using System.Security.AccessControl;
using System.Security.Principal;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>
/// ProgramData\zaprett as ARCHITECTURE-WIN §3 wants it, enforced by the service itself (running as SYSTEM): owner
/// Administrators, protected DACL — SYSTEM and Administrators full control, Users read. A directory created in
/// advance by a user (who would stay its owner and could rewrite the DACL) or a junction/symlink planted inside would
/// otherwise let that user redirect what SYSTEM writes and deletes (logs rotation, run\, installed\).
/// </summary>
public static class DataDirSecurity
{
    /// <summary>Throws when the data directory or one of its fixed subdirectories is a reparse point.</summary>
    public static void EnsureNoReparsePoints(IPaths paths)
    {
        foreach (var d in Dirs(paths))
        {
            var info = new DirectoryInfo(d);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"refusing to use {d}: it is a junction or symbolic link");
        }
    }

    public static DirectorySecurity BuildAcl()
    {
        var sec = new DirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        sec.SetOwner(admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>Service mode only: checks, creates and locks down the data directories.</summary>
    public static void Secure(WindowsPaths paths)
    {
        EnsureNoReparsePoints(paths);
        paths.EnsureDataDirs();
        // each one: a subdirectory created in advance by a user keeps that user as owner otherwise
        foreach (var d in Dirs(paths))
            new DirectoryInfo(d).SetAccessControl(BuildAcl());
        // checked again in case one was swapped for a link in between
        EnsureNoReparsePoints(paths);
    }

    private static IEnumerable<string> Dirs(IPaths p) => [p.DataDir, p.UserDir, p.InstalledDir, p.RunDir, p.LogDir];
}
