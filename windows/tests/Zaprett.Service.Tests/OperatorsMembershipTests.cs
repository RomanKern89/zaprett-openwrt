using System.Security.Principal;
using Zaprett.Service.Ipc;

namespace Zaprett.Service.Tests;

/// <summary>Group membership as it is now, when the logon token predates the installation (found on the Windows 10 test machine).</summary>
public class OperatorsMembershipTests
{
    private sealed class FakeMembership(Func<MembershipResult> answer) : ILocalGroupMembership
    {
        public List<(string Group, SecurityIdentifier User)> Calls { get; } = [];

        public MembershipResult IsDirectMember(string group, SecurityIdentifier user)
        {
            Calls.Add((group, user));
            return answer();
        }
    }

    // the tests run as a user whose token has no "zaprett Operators" (the group does not even exist here) and who is
    // not an elevated admin — exactly the first-run situation after msiexec
    private static bool Elevated()
    {
        using var me = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [Fact]
    public void MemberNow_TokenWithoutGroup_CanModify()
    {
        Assert.False(Elevated(), "run the tests unelevated");
        var fake = new FakeMembership(() => MembershipResult.Member);
        using var me = WindowsIdentity.GetCurrent();
        var caller = CallerResolver.FromIdentity(me, fake, new MemoryLog());
        Assert.True(caller.IsOperator);
        Assert.True(caller.CanModify);
        Assert.False(caller.IsAdmin);   // a filtered UAC admin is still no admin
        // asked for our group with the SID from the token
        Assert.Equal((CallerResolver.OperatorsGroup, me.User!), fake.Calls.Single());
    }

    [Theory]
    [InlineData(MembershipAnswer.NotMember)]
    [InlineData(MembershipAnswer.GroupMissing)]
    public void NotMemberOrNoGroup_CannotModify(MembershipAnswer answer)
    {
        Assert.False(Elevated(), "run the tests unelevated");
        var log = new MemoryLog();
        using var me = WindowsIdentity.GetCurrent();
        var caller = CallerResolver.FromIdentity(me, new FakeMembership(() => new MembershipResult(answer)), log);
        Assert.False(caller.CanModify);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void MembershipError_CannotModify_AndIsLogged()
    {
        Assert.False(Elevated(), "run the tests unelevated");
        using var me = WindowsIdentity.GetCurrent();
        var log = new MemoryLog();
        var caller = CallerResolver.FromIdentity(me, new FakeMembership(() => new MembershipResult(MembershipAnswer.Error, "rpc down")), log);
        Assert.False(caller.CanModify);
        Assert.Contains(log.Lines, l => l.StartsWith("WARN ipc: cannot read the members of \"zaprett Operators\"", StringComparison.Ordinal)
                                        && l.Contains("rpc down"));

        var log2 = new MemoryLog();
        var thrown = CallerResolver.FromIdentity(me, new FakeMembership(() => throw new System.ComponentModel.Win32Exception(5)), log2);
        Assert.False(thrown.CanModify);
        Assert.Single(log2.Lines);
    }

    [Fact]
    public async Task OverThePipe_UserAddedAfterLogon_CanModify()
    {
        // the real token of this test process over a real pipe, membership reported as current
        string pipe = "zaprett-test-" + Guid.NewGuid().ToString("N");
        var dispatcher = new FakeDispatcher();
        var fake = new FakeMembership(() => MembershipResult.Member);
        using var server = new PipeServer(dispatcher, p => CallerResolver.FromPipe(p, fake, new MemoryLog()), new MemoryLog(),
            new PipeServerOptions { PipeName = pipe });
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        await using (var c = new Zaprett.Ipc.ZaprettPipeClient(pipe, TimeSpan.FromSeconds(3)))
        {
            var r = await c.CallAsync("start");
            Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        }
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(me.User, fake.Calls.Single().User);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>The real NetLocalGroupGetMembers on this workstation, read only. Run with: --filter Category=System.</summary>
[Trait("Category", "System")]
public class NetLocalGroupMembershipSystemTests
{
    [Fact]
    public void RealGroups()
    {
        var m = new NetLocalGroupMembership();
        // the Administrators group by SID (its name is localized)
        string admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount)).Value.Split('\\')[^1];
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(MembershipAnswer.Member, m.IsDirectMember(admins, me.User!).Answer);
        Assert.Equal(MembershipAnswer.NotMember, m.IsDirectMember(admins, new SecurityIdentifier("S-1-5-21-1-2-3-4242")).Answer);
        Assert.Equal(MembershipAnswer.GroupMissing, m.IsDirectMember("zaprett-no-such-group-" + Guid.NewGuid().ToString("N")[..8], me.User!).Answer);
    }
}
