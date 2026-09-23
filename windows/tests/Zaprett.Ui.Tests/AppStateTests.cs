using System.Text.Json.Nodes;
using Zaprett.Ipc;
using Zaprett.Ui.Core.DevFakes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Tests;

public sealed class AppStateTests
{
    [Fact]
    public async Task Refresh_fills_status_presets_monitor_probe()
    {
        var (state, _, _) = Make.State();
        await state.RefreshAsync();
        Assert.Equal(ConnectionState.Available, state.Connection);
        Assert.True(state.Status.Bool("running"));
        Assert.NotEmpty(state.Presets.Objs("services"));
        Assert.Equal("ok", state.Monitor.Str("state"));
        Assert.Equal(48, state.Monitor.Arr("history").Count);
        Assert.NotNull(state.Probe);
    }

    [Fact]
    public async Task Unavailable_service_gives_service_unavailable_and_sets_the_state()
    {
        var (state, _, _) = Make.State(FakeZaprettClient.Scenarios.Unavailable);
        var e = await Assert.ThrowsAsync<ZaprettCallException>(() => state.CallAsync("status"));
        Assert.Equal("service_unavailable", e.Code);
        Assert.Equal(ConnectionState.Unavailable, state.Connection);
    }

    [Fact]
    public async Task Service_start_makes_it_available_again()
    {
        var (state, fake, _) = Make.State(FakeZaprettClient.Scenarios.Unavailable);
        var shell = new ShellViewModel(state);
        await Assert.ThrowsAsync<ZaprettCallException>(() => state.RefreshAsync());
        shell.Update();
        Assert.True(shell.IsServiceDown);
        await shell.StartServiceCommand.ExecuteAsync(null);
        Assert.True(fake.Available);
        Assert.False(shell.IsServiceDown);
        Assert.Equal("on", shell.TrayState);
    }

    [Fact]
    public async Task Ok_false_answer_becomes_an_exception_with_the_code()
    {
        var client = new ScriptedClient((_, _) => Make.Json("""{"ok":false,"error":"job_busy","message":"Уже выполняется"}"""));
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var e = await Assert.ThrowsAsync<ZaprettCallException>(() => state.CallAsync("probe"));
        Assert.Equal("job_busy", e.Code);
        Assert.Equal("Уже выполняется", e.ServiceMessage);
    }

    [Fact]
    public async Task Answer_without_ok_is_treated_as_an_error()
    {
        var client = new ScriptedClient((_, _) => Make.Json("""{"result":1}"""));
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        var e = await Assert.ThrowsAsync<ZaprettCallException>(() => state.CallAsync("status"));
        Assert.Equal("bad_answer", e.Code);
    }

    [Fact]
    public async Task Page_failure_falls_back_to_separate_calls()
    {
        var client = new ScriptedClient((m, _) => m switch
        {
            "page" => Make.Json("""{"ok":false,"error":"unknown_method"}"""),
            "status" => Make.Json("""{"ok":true,"enabled":true,"running":true,"strategy":{"id":"s1","name":"s1"}}"""),
            "presets" => Make.Json("""{"ok":true,"services":[]}"""),
            "monitor.status" => Make.Json("""{"ok":true,"monitor":{"state":"ok","enabled":true,"history":[]}}"""),
            "probe.status" => Make.Json("""{"ok":true,"probe":null}"""),
            "dns.status" => Make.Json("""{"ok":true,"dns":{"encrypted":false}}"""),
            _ => Make.Json("""{"ok":false,"error":"unknown_method"}"""),
        });
        var state = new AppState(client, new FakePlatform(), UiPrefs.Load(null));
        await state.RefreshAsync();
        Assert.True(state.Status.Bool("running"));
        Assert.Equal("ok", state.Monitor.Str("state"));
        Assert.Contains(client.Calls, c => c.Method == "status");
    }

    [Fact]
    public async Task Run_job_waits_until_the_job_finishes()
    {
        var (state, _, _) = Make.State();
        var updates = new List<long>();
        var (reply, job) = await state.RunJobAsync("probe", null, j => updates.Add(j.Long("progress")));
        Assert.NotNull(reply.Obj("job"));
        Assert.Equal("done", job.Str("state"));
        Assert.Equal(100, updates[^1]);
    }

    [Fact]
    public async Task Degradation_notifies_once()
    {
        L.SetLanguage("en");
        var (state, fake, platform) = Make.State();
        await state.RefreshAsync();
        Assert.Empty(platform.Notifications);
        fake.SetScenario(FakeZaprettClient.Scenarios.Degraded);
        await state.RefreshAsync();
        await state.RefreshAsync();
        Assert.Single(platform.Notifications);
        Assert.Equal(L.T("Toast.Degraded.Title"), platform.Notifications[0].Title);
    }

    [Fact]
    public async Task Strategy_replaced_by_the_service_notifies_but_own_change_does_not()
    {
        L.SetLanguage("en");
        var (state, _, platform) = Make.State();
        await state.RefreshAsync();
        state.NoteOwnStrategyChange();
        await state.CallAsync("strategy.set", new JsonObject { ["id"] = "strategy-alt" });
        await state.RefreshAsync();
        Assert.Empty(platform.Notifications);

        var (state2, fake2, platform2) = Make.State();
        await state2.RefreshAsync();
        await fake2.CallAsync("strategy.set", new JsonObject { ["id"] = "strategy-alt3" });
        await state2.RefreshAsync();
        Assert.Single(platform2.Notifications);
        Assert.Contains("strategy-alt3", platform2.Notifications[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_update_the_state()
    {
        var (state, _, _) = Make.State();
        state.ApplyEvent(new ServiceEvent("job", Make.Json("""{"id":"1","name":"test","state":"running","progress":30}""")));
        Assert.Equal(30, state.Job.Long("progress"));
        state.ApplyEvent(new ServiceEvent("status", Make.Json("""{"ok":true,"enabled":true,"running":false}""")));
        Assert.False(state.Status.Bool("running"));
        state.ApplyEvent(new ServiceEvent("monitor", Make.Json("""{"monitor":{"state":"repairing"}}""")));
        Assert.Equal("repairing", state.Monitor.Str("state"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Client_factory_uses_the_fake_only_when_asked()
    {
        string? NoEnv(string _) => null;
        Assert.IsType<FakeZaprettClient>(ClientFactory.Create(["--fake"], NoEnv));
        Assert.IsType<FakeZaprettClient>(ClientFactory.Create([], n => n == "ZAPRETT_UI_FAKE" ? "1" : null));
        Assert.Equal("degraded", ClientFactory.FakeScenario(["--fake=degraded"], NoEnv));
        Assert.False(ClientFactory.IsFake([], NoEnv));
    }

    [Fact]
    public async Task Client_factory_missing_type_gives_an_unavailable_client()
    {
        var client = ClientFactory.CreateByTypeName("No.Such.Type, No.Such.Assembly");
        Assert.IsType<UnavailableClient>(client);
        await Assert.ThrowsAsync<ZaprettUnavailableException>(() => client.CallAsync("status"));
    }

    [Fact]
    public void Client_factory_fills_constructor_defaults()
    {
        var client = ClientFactory.CreateByTypeName(typeof(WithDefaults).AssemblyQualifiedName!);
        var w = Assert.IsType<WithDefaults>(client);
        Assert.Equal("zaprett", w.Pipe);
    }

    public sealed class WithDefaults(string pipe = "zaprett", int timeout = 5) : IZaprettClient
    {
        public string Pipe { get; } = pipe;
        public int Timeout { get; } = timeout;
        public Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default) => Task.FromResult(new JsonObject());
        public IAsyncEnumerable<ServiceEvent> SubscribeAsync(CancellationToken ct = default) => AsyncEnumerable.Empty<ServiceEvent>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Prefs_survive_a_damaged_file_and_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zaprett-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ui.json");
            File.WriteAllText(path, "{ not json");
            var prefs = UiPrefs.Load(path);
            Assert.Equal("ru", prefs.Language);
            prefs.Language = "zh-CN";
            prefs.WizardDone = true;
            prefs.Save();
            var again = UiPrefs.Load(path);
            Assert.Equal("zh-CN", again.Language);
            Assert.True(again.WizardDone);
            File.WriteAllText(path, """{"language":"de","theme":"neon"}""");
            var bad = UiPrefs.Load(path);
            Assert.Equal("ru", bad.Language);
            Assert.Equal("system", bad.Theme);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
