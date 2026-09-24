using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>status.install_id tells a new installation (data removed with REMOVEDATA) from an old one, so the app's
/// first-run wizard is not skipped after a reinstall.</summary>
public sealed class InstallIdTests
{
    [Fact]
    public async Task IsAGuid_StableAcrossServiceRestarts_AndNewAfterTheDataIsRemoved()
    {
        using var h = new Harness();
        var id = R.Str((await h.Call("status"))["install_id"]);
        Assert.True(Guid.TryParseExact(id, "D", out _), id);
        Assert.Equal(id, R.Str((await h.Call("status"))["install_id"]));
        var file = Path.Combine(h.F.Paths.DataDir, "install-id");
        Assert.Equal(id, File.ReadAllText(file).Trim());
        // the service restarts (upgrade): a new core over the same data keeps the id
        var restarted = new CommandDispatcher(h.F.Services);
        var again = await restarted.InvokeAsync("status", null, CallerInfo.System, CancellationToken.None);
        Assert.Equal(id, R.Str(again["install_id"]));
        // uninstall with REMOVEDATA + a new installation: another id
        Directory.Delete(h.F.Paths.DataDir, true);
        var fresh = new CommandDispatcher(h.F.Services);
        var id2 = R.Str((await fresh.InvokeAsync("status", null, CallerInfo.System, CancellationToken.None))["install_id"]);
        Assert.True(Guid.TryParseExact(id2, "D", out _), id2);
        Assert.NotEqual(id, id2);
    }

    [Fact]
    public async Task ADamagedFile_IsReplaced()
    {
        using var h = new Harness();
        Directory.CreateDirectory(h.F.Paths.DataDir);
        File.WriteAllText(Path.Combine(h.F.Paths.DataDir, "install-id"), "not-a-guid\n");
        var id = R.Str((await h.Call("status"))["install_id"]);
        Assert.True(Guid.TryParseExact(id, "D", out _), id);
        Assert.Equal(id, File.ReadAllText(Path.Combine(h.F.Paths.DataDir, "install-id")).Trim());
    }
}
