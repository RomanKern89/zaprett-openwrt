using System.Text.Json.Nodes;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>The switch "update strategies and lists from the repository daily" (Settings and the wizard) writes
/// repo.autoupdate through settings.set and reads it back from settings.get.</summary>
public sealed class RepoAutoupdateSettingsTests
{
    static readonly CallerInfo Operator = new("op", false, true);

    static JsonObject Repo(JsonNode? value) => new() { ["repo"] = new JsonObject { ["autoupdate"] = value } };

    static async Task<bool> ReadAsync(Harness h) =>
        R.Bool((await h.Call("settings.get", null, Harness.User))["settings"]!["repo"]!["autoupdate"]);

    static bool OnDisk(Harness h) =>
        JsonNode.Parse(File.ReadAllText(h.F.Paths.ConfigFile))!["repo"]!["autoupdate"]!.GetValue<bool>();

    [Fact]
    public async Task AnOperator_SwitchesIt_BothWays_WithoutTouchingTheEngine()
    {
        using var h = new Harness();
        Assert.True(await ReadAsync(h));
        await h.Call("start");
        var engineLog = h.F.Engine.Log.Count;
        var off = await h.Call("settings.set", Repo(false), Operator);
        Assert.True(R.IsOk(off), off.ToJsonString());
        Assert.False(R.Bool(off["settings"]!["repo"]!["autoupdate"]));
        Assert.False(R.Bool(off["reloaded"]));
        Assert.False(await ReadAsync(h));
        Assert.False(OnDisk(h));
        Assert.Equal(engineLog, h.F.Engine.Log.Count);
        // a partial patch: the other repo keys stay as they were (set to non-defaults first, so a replaced object would show)
        Assert.True(R.IsOk(await h.Call("settings.set", new JsonObject
        {
            ["repo"] = new JsonObject { ["url"] = "https://example.org/zaprett/index.json", ["autoupdate_hour"] = 7 },
        })));
        Assert.True(R.IsOk(await h.Call("settings.set", Repo(false), Operator)));
        var repo = (await h.Call("settings.get", null, Harness.User))["settings"]!["repo"]!;
        Assert.Equal("https://example.org/zaprett/index.json", R.Str(repo["url"]));
        Assert.Equal(7L, R.Long(repo["autoupdate_hour"]));
        Assert.False(R.Bool(repo["autoupdate"]));
        var on = await h.Call("settings.set", Repo(true), Operator);
        Assert.True(R.IsOk(on), on.ToJsonString());
        Assert.True(await ReadAsync(h));
        Assert.True(OnDisk(h));
        // the daily job follows it at once (the job side is covered in RepoSourcesTests)
        Assert.True(h.D.Context.Config.Load().Repo.Autoupdate);
    }

    [Fact]
    public async Task AUserWithoutRights_IsRefused_AndNothingChanges()
    {
        using var h = new Harness();
        var r = await h.Call("settings.set", Repo(false), Harness.User);
        Assert.Equal("access_denied", R.Error(r));
        Assert.True(await ReadAsync(h));
        Assert.False((await h.Call("status", null, Harness.User))["can_modify"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ABadValue_IsRefused_AndNothingChanges()
    {
        using var h = new Harness();
        await h.Call("settings.set", Repo(false), Operator);
        var r = await h.Call("settings.set", Repo("maybe"), Operator);
        Assert.Equal("bad_value", R.Error(r));
        Assert.Contains("autoupdate", R.Strings(r["details"]!["bad_options"]));
        Assert.False(await ReadAsync(h));
    }

    [Fact]
    public async Task OnlyTheRepositoryUrl_IsForAdministrators_NotTheSwitch()
    {
        using var h = new Harness();
        Assert.Equal("access_denied", R.Error(await h.Call("settings.set",
            new JsonObject { ["repo"] = new JsonObject { ["url"] = "https://example.org/index.json" } }, Operator)));
        Assert.True(R.IsOk(await h.Call("settings.set", Repo(false), Operator)));
    }
}
