using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

public sealed class ServiceChoiceTests
{
    private static ServiceChoice Make(bool enabled, Dictionary<string, JsonObject>? items = null) =>
        new(Tests.Make.Json($$"""
            {"id":"youtube","name":"YouTube","works":"yes","enabled":{{(enabled ? "true" : "false")}},
             "lists":["zaprett-youtube"],"ipsets":["zaprett-youtube-ip"],"sources":[]}
            """), null, items);

    [Fact]
    public void State_is_shown_only_when_it_differs_from_the_choice()
    {
        L.SetLanguage("ru");
        var off = Make(enabled: false);
        off.IsSelected = false;
        Assert.False(off.HasStateLabel);
        off.IsSelected = true;
        Assert.True(off.HasStateLabel);
        Assert.Equal("сейчас не включён", off.StateLabel);

        var on = Make(enabled: true);
        Assert.True(on.IsSelected);
        Assert.False(on.HasStateLabel);
        on.IsSelected = false;
        Assert.Equal("сейчас включён", on.StateLabel);
    }

    [Fact]
    public void Contents_show_item_names_and_keep_ids_for_the_tooltip()
    {
        L.SetLanguage("en");
        try
        {
            var items = new Dictionary<string, JsonObject>
            {
                ["zaprett-youtube"] = Tests.Make.Json("""{"id":"zaprett-youtube","name":"YouTube: сайты","name_en":"YouTube: sites"}"""),
            };
            var c = Make(enabled: false, items);
            // installed item → its name; unknown item → its id
            Assert.Equal(L.F("Service.Contents", "YouTube: sites, zaprett-youtube-ip"), c.Contents);
            Assert.Equal("zaprett-youtube, zaprett-youtube-ip", c.ContentsIds);
        }
        finally
        {
            L.SetLanguage("ru");
        }
    }
}
