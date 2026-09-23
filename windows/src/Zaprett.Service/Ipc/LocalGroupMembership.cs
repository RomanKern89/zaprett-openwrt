using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Zaprett.Service.Ipc;

public enum MembershipAnswer
{
    Member,
    NotMember,
    GroupMissing,
    Error,
}

public sealed record MembershipResult(MembershipAnswer Answer, string? Error = null)
{
    public static MembershipResult Member { get; } = new(MembershipAnswer.Member);
    public static MembershipResult NotMember { get; } = new(MembershipAnswer.NotMember);
    public static MembershipResult GroupMissing { get; } = new(MembershipAnswer.GroupMissing);
}

/// <summary>Current membership of a local group (not the logon token, which is frozen at logon).</summary>
public interface ILocalGroupMembership
{
    MembershipResult IsDirectMember(string group, SecurityIdentifier user);
}

/// <summary>
/// <see cref="ILocalGroupMembership"/> by NetLocalGroupGetMembers level 0: the member SIDs of the group as they are now,
/// compared by SID (no names, no text). Only direct members count; a member of a nested group is recognized through the
/// token (after its next logon). No cache: removal from the group applies to the next connection.
/// </summary>
public sealed partial class NetLocalGroupMembership : ILocalGroupMembership
{
    private const int NERR_Success = 0;
    private const int ERROR_MORE_DATA = 234;
    private const int NERR_GroupNotFound = 2220;
    private const int ERROR_NO_SUCH_ALIAS = 1376;
    private const int MAX_PREFERRED_LENGTH = -1;

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out nint buffer,
        int prefMaxLen, out int entriesRead, out int totalEntries, ref nint resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);

    public MembershipResult IsDirectMember(string group, SecurityIdentifier user)
    {
        nint resume = 0;
        while (true)
        {
            int rc = NetLocalGroupGetMembers(null, group, 0, out nint buf, MAX_PREFERRED_LENGTH, out int read, out _, ref resume);
            try
            {
                if (rc == NERR_GroupNotFound || rc == ERROR_NO_SUCH_ALIAS)
                    return MembershipResult.GroupMissing;
                if (rc != NERR_Success && rc != ERROR_MORE_DATA)
                    return new MembershipResult(MembershipAnswer.Error, $"NetLocalGroupGetMembers({group}) failed: {rc}");
                // LOCALGROUP_MEMBERS_INFO_0 = { PSID lgrmi0_sid }
                for (int i = 0; i < read; i++)
                {
                    nint sid = Marshal.ReadIntPtr(buf, i * IntPtr.Size);
                    if (sid != 0 && new SecurityIdentifier(sid).Equals(user))
                        return MembershipResult.Member;
                }
                if (rc != ERROR_MORE_DATA)
                    return MembershipResult.NotMember;
            }
            finally
            {
                if (buf != 0)
                    _ = NetApiBufferFree(buf);
            }
        }
    }
}
