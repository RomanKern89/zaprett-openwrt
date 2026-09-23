using System.Text.Json.Nodes;
using Zaprett.Ipc;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;

// The interface language is global (L), so tests run one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Zaprett.Ui.Tests;

/// <summary>Desktop services that record what the view models asked for.</summary>
public sealed class FakePlatform : IUiPlatform
{
    public List<(string Title, string Text)> Notifications { get; } = [];
    public List<string> Copied { get; } = [];
    public bool ConfirmAnswer { get; set; } = true;
    public string? OpenText { get; set; }
    public string? SavedText { get; private set; }
    public int ServiceStarts { get; private set; }

    public Task<bool> StartServiceElevatedAsync()
    {
        ServiceStarts++;
        return Task.FromResult(false);
    }

    public void CopyText(string text) => Copied.Add(text);

    public Task<string?> SaveTextAsync(string suggestedName, string text)
    {
        SavedText = text;
        return Task.FromResult<string?>(@"C:\tmp\" + suggestedName);
    }

    public Task<string?> OpenTextAsync() => Task.FromResult(OpenText);

    public void Notify(string title, string text) => Notifications.Add((title, text));

    public Task<bool> ConfirmAsync(string title, string text, string primary) => Task.FromResult(ConfirmAnswer);

    public void ApplyTheme(string theme)
    {
    }

    public void ApplyLanguage(string language) => L.SetLanguage(language);
}

/// <summary>A client whose answers are given by a function (for error mapping tests).</summary>
public sealed class ScriptedClient(Func<string, JsonObject?, JsonObject> answer) : IZaprettClient
{
    public List<(string Method, JsonObject? Args)> Calls { get; } = [];

    public Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default)
    {
        Calls.Add((method, args));
        return Task.FromResult(answer(method, args));
    }

    public async IAsyncEnumerable<ServiceEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class Make
{
    /// <summary>Shared state over the fake service with fast jobs and polling.</summary>
    public static (AppState State, FakeZaprettClient Fake, FakePlatform Platform) State(string scenario = FakeZaprettClient.Scenarios.Running)
    {
        var fake = new FakeZaprettClient(scenario) { JobSeconds = 0.02 };
        var platform = new FakePlatform();
        var state = new AppState(fake, platform, UiPrefs.Load(null)) { JobPoll = TimeSpan.FromMilliseconds(5) };
        return (state, fake, platform);
    }

    public static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;
}

