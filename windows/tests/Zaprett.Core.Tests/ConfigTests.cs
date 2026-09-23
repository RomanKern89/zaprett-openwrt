using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Defaults_WhenDocumentEmpty()
    {
        var c = ConfigLoader.Normalize(null);
        Assert.False(c.Enabled);
        Assert.Equal("winws", c.Engine);
        Assert.Equal("strategy-general", c.Strategy);
        Assert.Equal(["zaprett-youtube", "zaprett-discord", "user-hosts"], c.Lists);
        Assert.Equal(["zaprett-exclude-ipset", "user-ipset-exclude"], c.ExcludeIpsets);
        Assert.Empty(c.BadOptions);
        Assert.Empty(c.Warnings);
        Assert.Equal(4, c.Sources.Count);
        Assert.True(c.Monitor.Present && c.Monitor.Enabled);
        Assert.Equal(30, c.Monitor.Interval);
        Assert.Equal("system", c.Dns.Mode);
        Assert.Equal("stable", c.Update.Channel);
        Assert.Equal(ConfigDefaults.RepoUrl, c.Repo.Url);
    }

    [Fact]
    public void DefaultDocument_NormalizesWithoutBadOptions()
    {
        var c = ConfigLoader.Normalize(ConfigDefaults.Document());
        Assert.Empty(c.BadOptions);
        Assert.Equal(ConfigDocument.From(ConfigLoader.Normalize(null)).ToJsonString(), ConfigDocument.From(c).ToJsonString());
    }

    [Fact]
    public void InvalidValues_FallBackAndAreReported()
    {
        var doc = new JsonObject
        {
            ["main"] = new JsonObject
            {
                ["engine"] = "nfqws", ["list_mode"] = "gray", ["lists"] = new JsonArray("ok-id", "bad id"), ["ipv6"] = "maybe",
                ["game_ports_tcp"] = "~1", ["strategy"] = "bad strategy", ["network_filter"] = new JsonObject { ["mode"] = "ssids", ["ssids"] = new JsonArray() },
            },
            ["repo"] = new JsonObject { ["url"] = "http://insecure.example/index.json", ["autoupdate_hour"] = 24 },
            ["test"] = new JsonObject { ["timeout"] = 0 },
            ["monitor"] = new JsonObject { ["interval"] = 25, ["threshold"] = "x" },
            ["dns"] = new JsonObject { ["mode"] = "dot" },
            ["update"] = new JsonObject { ["channel"] = "nightly" },
        };
        var c = ConfigLoader.Normalize(doc);
        Assert.Equal("winws", c.Engine);
        Assert.Equal("whitelist", c.ListMode);
        Assert.Equal(["ok-id"], c.Lists);
        Assert.False(c.Ipv6);
        Assert.Equal("1024-65535", c.GamePortsTcp);
        Assert.Equal("", c.Strategy);
        Assert.Equal("all", c.NetworkFilter.Mode);
        Assert.Equal(ConfigDefaults.RepoUrl, c.Repo.Url);
        Assert.Equal(4, c.Repo.AutoupdateHour);
        Assert.Equal(5, c.Test.Timeout);
        Assert.Equal(30, c.Monitor.Interval);
        Assert.Equal(3, c.Monitor.Threshold);
        Assert.Equal("system", c.Dns.Mode);
        Assert.Equal("stable", c.Update.Channel);
        foreach (var o in new[] { "engine", "list_mode", "lists", "ipv6", "game_ports_tcp", "strategy", "network_filter.ssids", "url",
                     "autoupdate_hour", "timeout", "monitor.interval", "monitor.threshold", "dns.mode", "update.channel" })
            Assert.Contains(o, c.BadOptions);
        Assert.Equal(["bad_config"], c.Warnings);
    }

    [Fact]
    public void RouterStyleStrings_AreAccepted()
    {
        var c = ConfigLoader.Normalize(new JsonObject
        {
            ["main"] = new JsonObject { ["enabled"] = "1", ["ipv6"] = "on", ["debug"] = "0", ["game_ports_udp"] = "" },
            ["test"] = new JsonObject { ["timeout"] = "7" },
        });
        Assert.True(c.Enabled);
        Assert.True(c.Ipv6);
        Assert.Equal("", c.GamePortsUdp);
        Assert.Equal(7, c.Test.Timeout);
        Assert.Empty(c.BadOptions);
    }

    [Fact]
    public void NetworkFilter_Ssids()
    {
        var c = ConfigLoader.Normalize(new JsonObject
        {
            ["main"] = new JsonObject
            {
                ["network_filter"] = new JsonObject { ["mode"] = "ssids", ["ssids"] = new JsonArray("Home", "bad,comma"), ["skip_corporate"] = true },
            },
        });
        Assert.Equal("ssids", c.NetworkFilter.Mode);
        Assert.Equal(["Home"], c.NetworkFilter.Ssids);
        Assert.True(c.NetworkFilter.SkipCorporate);
        Assert.Contains("network_filter.ssids", c.BadOptions);
    }

    [Fact]
    public void MonitorSection_NullMeansOff()
    {
        var c = ConfigLoader.Normalize(new JsonObject { ["monitor"] = null });
        Assert.False(c.Monitor.Present);
        Assert.False(c.Monitor.Enabled);
    }

    [Fact]
    public void Sources_AreValidated()
    {
        var s = ConfigLoader.NormalizeSource("mine", new JsonObject
        {
            ["type"] = "ipset", ["url"] = "https://example.com/l.txt", ["min_valid_ratio"] = "0.5", ["interval_hours"] = 0, ["enabled"] = true,
        });
        Assert.True(s.Valid);
        Assert.True(s.Enabled);
        Assert.Equal(0.5, s.MinValidRatio);
        Assert.Equal(72, s.IntervalHours);
        Assert.Contains("interval_hours", s.BadOptions);
        Assert.Equal("mine", s.Title);
        Assert.False(ConfigLoader.NormalizeSource("Bad-Name", new JsonObject { ["url"] = "https://e.com/" }).Valid);
        Assert.False(ConfigLoader.NormalizeSource("x", new JsonObject { ["url"] = "http://e.com/" }).Valid);
        Assert.False(ConfigLoader.NormalizeSource("x", new JsonObject { ["url"] = "https://e.com/", ["type"] = "bin" }).Valid);
        Assert.Contains("min_valid_ratio", ConfigLoader.NormalizeSource("x", new JsonObject { ["min_valid_ratio"] = 2 }).BadOptions);
        Assert.Empty(ConfigLoader.Normalize(new JsonObject { ["sources"] = new JsonObject() }).Sources);
    }

    [Fact]
    public void Store_WritesAtomicallyWithBackup_AndRecoversDamage()
    {
        using var p = new TestPaths();
        var store = new ConfigStore(p.ConfigFile);
        Assert.False(store.Load().Enabled);
        Assert.True(store.Set("main", new JsonObject { ["enabled"] = true }));
        Assert.True(store.Load().Enabled);
        Assert.False(File.Exists(p.ConfigFile + ".bak"));
        Assert.True(store.Set("main", new JsonObject { ["list_mode"] = "blacklist" }));
        Assert.True(File.Exists(p.ConfigFile + ".bak"));
        Assert.Empty(Directory.GetFiles(p.DataDir, "*.tmp.*"));
        var raw = File.ReadAllBytes(p.ConfigFile);
        Assert.DoesNotContain((byte)13, raw);

        File.WriteAllText(p.ConfigFile, "{ broken");
        var c = store.Load();
        Assert.True(c.Enabled);
        Assert.Equal("whitelist", c.ListMode);
        Assert.Contains("config.json", c.BadOptions);

        Assert.True(store.Set("main", new JsonObject { ["enabled"] = null }));
        Assert.False(store.Load().Enabled);
    }

    [Fact]
    public void Store_PreviewDoesNotWrite_AndSourcesCanBeEdited()
    {
        using var p = new TestPaths();
        var store = new ConfigStore(p.ConfigFile);
        var prev = store.Preview("main", new JsonObject { ["list_mode"] = "blacklist" });
        Assert.Equal("blacklist", prev.ListMode);
        Assert.False(File.Exists(p.ConfigFile));
        Assert.True(store.SetSource("mine", new JsonObject { ["type"] = "list", ["url"] = "https://e.com/x" }));
        Assert.Contains(store.Load().Sources, s => s.Name == "mine" && s.Valid);
        Assert.Equal(5, store.Load().Sources.Count);
        Assert.True(store.DeleteSource("refilter_domains"));
        Assert.False(store.DeleteSource("refilter_domains"));
        Assert.Equal(4, store.Load().Sources.Count);
    }

    [Fact]
    public void Merge_IsDeep()
    {
        var t = new JsonObject { ["main"] = new JsonObject { ["a"] = 1, ["b"] = 2 }, ["x"] = 1 };
        ConfigStore.Merge(t, new JsonObject { ["main"] = new JsonObject { ["b"] = 3, ["a"] = null }, ["y"] = new JsonArray(1) });
        Assert.Equal("{\"main\":{\"b\":3},\"x\":1,\"y\":[1]}", t.ToJsonString());
    }

    [Fact]
    public void Document_RoundTrips()
    {
        var c = ConfigLoader.Normalize(new JsonObject { ["main"] = new JsonObject { ["engine"] = "winws2", ["quic_block"] = true } });
        var again = ConfigLoader.Normalize(ConfigDocument.From(c));
        Assert.Equal(ConfigDocument.From(c).ToJsonString(), ConfigDocument.From(again).ToJsonString());
        Assert.Equal("winws2", again.Engine);
        Assert.Equal("strategy_winws2", Engines.StrategyOption("winws2"));
        Assert.Equal("nfqws2", Engines.ItemType("winws2"));
        Assert.Equal("", c.CurrentStrategy("winws2"));
        Assert.Empty(c.ListOption("nope"));
        Assert.True(R.IsOk(R.Ok()));
    }
}
