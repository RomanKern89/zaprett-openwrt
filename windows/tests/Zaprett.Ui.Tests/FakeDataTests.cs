using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;

namespace Zaprett.Ui.Tests;

/// <summary>The fake takes the texts of bundled items and services from the real data of the product.</summary>
public sealed class FakeDataTests
{
    [Fact]
    public async Task Bundled_items_carry_the_translations_of_their_real_manifests()
    {
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        var items = (await fake.CallAsync("items")).Objs("items").ToList();
        var voice = items.Single(i => i.Str("id") == "zaprett-discord-voice");
        // description comes only from the manifest: the fake itself leaves it empty
        Assert.False(string.IsNullOrEmpty(voice.Str("description_zh")));
        Assert.False(string.IsNullOrEmpty(voice.Str("description_en")));
        L.SetLanguage("zh-CN");
        try
        {
            Assert.Equal(voice.Str("name_zh"), L.Pick(voice, "name"));
            Assert.Contains("语音", L.Pick(voice, "name"), StringComparison.Ordinal);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }

    [Fact]
    public async Task Items_without_a_manifest_keep_the_texts_of_the_fake()
    {
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        var items = (await fake.CallAsync("items")).Objs("items").ToList();
        var own = items.Single(i => i.Str("id") == "user-my-youtube");
        Assert.Equal("user-my-youtube", own.Str("name"));
        Assert.Null(own.Str("name_zh"));
    }

    [Fact]
    public async Task Services_come_from_the_real_presets_with_chinese_texts()
    {
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        var services = (await fake.CallAsync("presets")).Objs("services").ToList();
        Assert.NotEmpty(services);
        Assert.Contains(services, s => !string.IsNullOrEmpty(s.Str("description_zh")));
    }

    [Fact]
    public async Task The_fake_reports_the_version_of_this_build()
    {
        // the screenshots show it in the settings and in the log: it must not stay at an old release
        var props = File.ReadAllText(Path.Combine(Repo.Root, "Directory.Build.props"));
        var version = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        await using var fake = new FakeZaprettClient { JobSeconds = 0.01 };
        Assert.Equal(version, (await fake.CallAsync("version")).Str("version"));
        Assert.Equal(version, (await fake.CallAsync("status")).Str("version"));
        Assert.Contains("version " + version, (await fake.CallAsync("log", new System.Text.Json.Nodes.JsonObject { ["lang"] = "en" })).ToJsonString(), StringComparison.Ordinal);
    }
}
