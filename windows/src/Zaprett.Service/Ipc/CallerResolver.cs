using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Zaprett.Core;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Ipc;

/// <summary>
/// Who is on the other end of the pipe, from the client token (ARCHITECTURE-WIN §6). Admin = SYSTEM or an
/// <em>enabled</em> BUILTIN\Administrators group (elevated); a filtered UAC token, where the group is deny-only, is not
/// an admin (only through the group). Operator = the group "zaprett Operators" (created by the MSI) is in the token, OR
/// the caller's user SID — taken from the token, never from the request — is a direct member of the group right now
/// (NetLocalGroupGetMembers): the logon token of a user added by the installer does not have the group until the next
/// logon. Anything that cannot be read (anonymous client, broken token, failed membership query) is not a privilege.
/// </summary>
public static class CallerResolver
{
    public const string OperatorsGroup = "zaprett Operators";

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    public static CallerInfo Anonymous { get; } = new("anonymous", false, false);

    // load and JIT everything the impersonated part needs before any client connects
    static CallerResolver() => CaptureToken().Dispose();

    private static WindowsIdentity CaptureToken() => WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);

    private static readonly ILocalGroupMembership DefaultMembership = new NetLocalGroupMembership();

    public static CallerInfo FromPipe(NamedPipeServerStream pipe) => FromPipe(pipe, DefaultMembership, null);

    public static CallerInfo FromPipe(NamedPipeServerStream pipe, ILocalGroupMembership membership, ILog? log)
    {
        try
        {
            // only take the token while impersonating: at the Identification level the thread cannot open files, and a
            // failed assembly load there is cached for the whole process
            WindowsIdentity? id = null;
            pipe.RunAsClient(() => id = CaptureToken());
            if (id is null)
                return Anonymous;
            using (id)
                return FromIdentity(id, membership, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return Anonymous;
        }
    }

    public static CallerInfo FromIdentity(WindowsIdentity id) => FromIdentity(id, DefaultMembership, null);

    public static CallerInfo FromIdentity(WindowsIdentity id, ILocalGroupMembership membership, ILog? log)
    {
        if (id.IsAnonymous || id.User is null)
            return Anonymous;
        var principal = new WindowsPrincipal(id);
        bool admin = id.User.Equals(LocalSystem) || principal.IsInRole(Administrators);
        var operators = OperatorsSid();
        bool op = operators is not null && principal.IsInRole(operators);
        if (!op && !admin)
            op = IsCurrentMember(id.User, membership, log);
        return new CallerInfo(id.Name, admin, op);
    }

    /// <summary>Is the user a direct member of "zaprett Operators" now (not at logon)?</summary>
    private static bool IsCurrentMember(SecurityIdentifier user, ILocalGroupMembership membership, ILog? log)
    {
        MembershipResult r;
        try
        {
            r = membership.IsDirectMember(OperatorsGroup, user);
        }
        catch (Exception e) when (e is ExternalException or ArgumentException or InvalidOperationException or DllNotFoundException
                                      or EntryPointNotFoundException)
        {
            r = new MembershipResult(MembershipAnswer.Error, e.Message);
        }
        if (r.Answer == MembershipAnswer.Error)
            log?.Warn($"ipc: cannot read the members of \"{OperatorsGroup}\" for {user}: {r.Error}; not an operator");
        return r.Answer == MembershipAnswer.Member;
    }

    /// <summary>SID of "zaprett Operators", or null when the group does not exist.</summary>
    public static SecurityIdentifier? OperatorsSid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, OperatorsGroup).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    /// <summary>Pipe DACL of §6: SYSTEM and Administrators — full control; Interactive users — read/write data only
    /// (no FILE_CREATE_PIPE_INSTANCE, so they cannot add instances to our pipe); plus the account the server runs
    /// under (in console mode that is the developer, who must be able to create the next instances).</summary>
    public static PipeSecurity CreatePipeSecurity()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(Administrators, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        using var self = WindowsIdentity.GetCurrent();
        if (self.User is { } user && !user.Equals(LocalSystem))
            ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }
}
