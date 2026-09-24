using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Zaprett.Ipc;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Text;

namespace Zaprett.Ui.Core.Services;

/// <summary>
/// The single view of the service shared by all pages: connection state and the last answers of status, presets,
/// monitor, probe and job. Keeps them fresh with the event stream ("subscribe") and falls back to polling
/// (ARCHITECTURE-WIN §6: no more often than every 30 s while the subscription works).
/// All members are used from the UI thread; awaits resume there through its synchronization context.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private readonly IZaprettClient _client;
    private readonly IUiPlatform _platform;
    private readonly UiPrefs _prefs;
    private string? _lastStrategy;
    private string? _lastMonitorState;
    private DateTimeOffset _ownStrategyChangeUntil;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Where unexpected failures of the background loops go (the app sets it to its log file).</summary>
    public static Action<string>? Log { get; set; }

    public AppState(IZaprettClient client, IUiPlatform platform, UiPrefs prefs)
    {
        _client = client;
        _platform = platform;
        _prefs = prefs;
    }

    public UiPrefs Prefs => _prefs;

    public IUiPlatform Platform => _platform;

    public bool IsFake => _client is DevFakes.FakeZaprettClient;

    public DevFakes.FakeZaprettClient? Fake => _client as DevFakes.FakeZaprettClient;

    /// <summary>Polling intervals; tests shorten them.</summary>
    public TimeSpan PollWithEvents { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollWithoutEvents { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollUnavailable { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan JobPoll { get; set; } = TimeSpan.FromSeconds(1);

    [ObservableProperty] public partial ConnectionState Connection { get; set; } = ConnectionState.Connecting;
    [ObservableProperty] public partial string? UnavailableReason { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModify))]
    public partial JsonObject? Status { get; set; }

    /// <summary>status.can_modify of this user (<see cref="ServiceAccess.CanModify"/>).</summary>
    public bool CanModify => ServiceAccess.CanModify(Status);
    [ObservableProperty] public partial JsonObject? Presets { get; set; }
    [ObservableProperty] public partial JsonObject? Monitor { get; set; }
    [ObservableProperty] public partial JsonObject? Probe { get; set; }
    [ObservableProperty] public partial JsonObject? Job { get; set; }
    [ObservableProperty] public partial JsonObject? Dns { get; set; }
    [ObservableProperty] public partial bool EventsActive { get; set; }

    public bool IsAvailable => Connection == ConnectionState.Available;

    /// <summary>Raised after any of the shared answers changed (once per refresh or event).</summary>
    public event EventHandler? Changed;

    /// <summary>Calls a method; an {ok:false} answer or a transport failure becomes ZaprettCallException.</summary>
    public async Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default)
    {
        // a user without the rights never sends a change: the service would refuse it anyway, and the controls are
        // locked already; this catches any path the controls missed
        if (!CanModify && ServiceAccess.Modifies(method))
        {
            Log?.Invoke($"call {method}: not sent, this user may only read (status.can_modify=false)");
            throw new ZaprettCallException("access_denied", null);
        }
        // every call carries the interface language, so messages of the service come in it (ARCHITECTURE-WIN §12.2)
        var withLang = args?.Clone() ?? [];
        withLang["lang"] = L.Language;
        JsonObject reply;
        try
        {
            reply = await _client.CallAsync(method, withLang, ct);
        }
        catch (ZaprettUnavailableException e)
        {
            SetUnavailable(e.Message);
            throw new ZaprettCallException("service_unavailable", null, null, e);
        }
        catch (TimeoutException e)
        {
            throw new ZaprettCallException("timeout", null, null, e);
        }
        if (Connection != ConnectionState.Available)
        {
            Connection = ConnectionState.Available;
            UnavailableReason = null;
        }
        if (!reply.IsOk())
            throw ZaprettCallException.From(reply);
        return reply;
    }

    /// <summary>
    /// "Turn the bypass on when Windows starts": main.autostart, separate from "on now" (status.enabled). The method
    /// "autostart" changes only it and never starts or stops the engine. The service says it has the method with
    /// status.autostart_separate=true; an older one (no field) gets the old enable/disable, which set both and stop the
    /// bypass on disable. Without a status yet the method is tried, and unknown_method falls back the same way.
    /// Returns the value now in force.
    /// </summary>
    public async Task<bool> SetAutostartAsync(bool enable, CancellationToken ct = default)
    {
        if (Status != null && !Status.Bool("autostart_separate"))
        {
            await CallAsync(enable ? "enable" : "disable", null, ct);
            return enable;
        }
        try
        {
            var r = await CallAsync("autostart", new JsonObject { ["enable"] = enable }, ct);
            return r["autostart"] is JsonValue ? r.Bool("autostart") : enable;
        }
        catch (ZaprettCallException e) when (e.Code == "unknown_method")
        {
            Log?.Invoke("autostart: the service has no method \"autostart\", using " + (enable ? "enable" : "disable"));
            await CallAsync(enable ? "enable" : "disable", null, ct);
            return enable;
        }
    }

    /// <summary>status.autostart, or "enabled" of a service that does not report it.</summary>
    public static bool AutostartOf(JsonObject? status) =>
        status?["autostart"] is JsonValue ? status.Bool("autostart") : status.Bool("enabled");

    /// <summary>Remembers that the next change of the strategy is made by this interface (no "strategy replaced"
    /// notification for it).</summary>
    public void NoteOwnStrategyChange() => _ownStrategyChangeUntil = DateTimeOffset.UtcNow.AddMinutes(2);

    /// <summary>Reloads the shared answers with one "page overview" call, or with separate calls if "page" fails.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        JsonObject? page = null;
        try
        {
            page = await CallAsync("page", new JsonObject { ["name"] = "overview" }, ct);
        }
        catch (ZaprettCallException e) when (e.Code != "service_unavailable")
        {
            page = null;
        }

        JsonObject? Part(string key) => page?.Obj(key) is { } p && p.IsOk() ? p : null;

        var status = Part("status") ?? await CallAsync("status", null, ct);
        var presets = Part("presets") ?? Presets ?? await TryCallAsync("presets", ct);
        var monitor = (Part("monitor") ?? await TryCallAsync("monitor.status", ct)).Obj("monitor");
        var probe = (Part("probe") ?? await TryCallAsync("probe.status", ct)).Obj("probe");
        var dns = (Part("dns") ?? await TryCallAsync("dns.status", ct)).Obj("dns") ?? status.Obj("dns");

        Apply(status: status, presets: presets, monitor: monitor, probe: probe, dns: dns, job: page?.Obj("job").Obj("job") ?? status.Obj("job"));
    }

    private async Task<JsonObject?> TryCallAsync(string method, CancellationToken ct)
    {
        try
        {
            return await CallAsync(method, null, ct);
        }
        catch (ZaprettCallException e) when (e.Code != "service_unavailable")
        {
            return null;
        }
    }

    /// <summary>Installed lists, IP networks and strategies by id: service cards show their names, not ids.</summary>
    public IReadOnlyDictionary<string, JsonObject> Items { get; private set; } = new Dictionary<string, JsonObject>();

    /// <summary>Whether the service can update the program (null: not asked yet in this session). A service without
    /// updates answers update.* with not_supported (0.1.0: UpdateControlStub); the answer is asked once per session.</summary>
    public bool? UpdatesSupported { get; set; }

    /// <summary>Reloads only the presets (after the wizard or list changes) and the names of the items they use.</summary>
    public async Task RefreshPresetsAsync(CancellationToken ct = default)
    {
        Presets = await CallAsync("presets", null, ct);
        // names are a convenience: without them the cards show ids, so a failure here is not an error
        var items = await TryCallAsync("items", ct);
        if (items != null)
            Items = items.Objs("items").Where(i => i.Str("id") != null).GroupBy(i => i.Str("id")!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a service event. A "status" event carries only a part of the status ({running, pid} or
    /// {engine:{…}} from the service), so it is merged into a copy and a full refresh is requested.</summary>
    public void ApplyEvent(ServiceEvent ev)
    {
        switch (ev.Type)
        {
            case "status":
                if (Status != null && ev.Data.Get("running") != null)
                {
                    var merged = Status.Clone();
                    merged["running"] = ev.Data.Bool("running");
                    merged["pid"] = ev.Data.Get("pid")?.DeepClone();
                    Apply(status: merged);
                }
                RequestRefresh();
                break;
            case "job":
                Apply(job: ev.Data.Obj("job") ?? ev.Data);
                break;
            case "probe":
                Apply(probe: ev.Data.Obj("probe") ?? ev.Data);
                break;
            case "monitor":
                Apply(monitor: ev.Data.Obj("monitor") ?? ev.Data);
                break;
        }
    }

    private void Apply(JsonObject? status = null, JsonObject? presets = null, JsonObject? monitor = null,
        JsonObject? probe = null, JsonObject? dns = null, JsonObject? job = null)
    {
        if (status != null)
        {
            Status = status;
            DetectStrategyReplaced(status);
            if (job == null)
                Job = status.Obj("job") ?? Job;
        }
        if (presets != null)
            Presets = presets;
        if (monitor != null)
        {
            Monitor = monitor;
            DetectDegraded(monitor);
        }
        if (probe != null)
            Probe = probe;
        if (dns != null)
            Dns = dns;
        if (job != null)
            Job = job;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void DetectStrategyReplaced(JsonObject status)
    {
        var id = status.Obj("strategy").Str("id");
        if (_lastStrategy != null && id != null && id != _lastStrategy && DateTimeOffset.UtcNow > _ownStrategyChangeUntil && _prefs.Notifications)
            _platform.Notify(L.T("Toast.StrategyReplaced.Title"),
                L.F("Toast.StrategyReplaced.Text", L.Pick(status.Obj("strategy"), "name") ?? id));
        _lastStrategy = id ?? _lastStrategy;
    }

    private void DetectDegraded(JsonObject monitor)
    {
        var state = monitor.Str("state");
        if (state == "degraded" && _lastMonitorState is not (null or "degraded" or "repairing") && _prefs.Notifications)
            _platform.Notify(L.T("Toast.Degraded.Title"),
                UiText.RepairBlockedByConflict(monitor) ? L.T("Toast.Degraded.TextConflict")
                : monitor.Bool("auto_repair") ? L.T("Toast.Degraded.TextRepair") : L.T("Toast.Degraded.Text"));
        _lastMonitorState = state;
    }

    /// <summary>Wakes the polling loop for an immediate refresh.</summary>
    public void RequestRefresh() => _wake.TrySetResult();

    private void SetUnavailable(string reason)
    {
        Connection = ConnectionState.Unavailable;
        UnavailableReason = reason;
        EventsActive = false;
    }

    /// <summary>ui.language of config.json is the language shared by the service, the CLI and the interface: after
    /// connecting, the interface takes it over when it differs from the last known one.</summary>
    public async Task SyncLanguageAsync(CancellationToken ct = default)
    {
        // a user without the rights cannot change the shared language: the one chosen for themselves stays
        if (!CanModify && _prefs.LanguagePersonal)
            return;
        var settings = await CallAsync("settings.get", null, ct);
        var doc = settings.Obj("settings") ?? settings.Obj("config");
        var lang = doc.Obj("ui").Str("language");
        if (lang == null)
            return;
        lang = L.Normalize(lang);
        if (lang != L.Language)
        {
            _prefs.Language = lang;
            _prefs.Save();
            _platform.ApplyLanguage(lang);
        }
    }

    /// <summary>Keeps the state fresh until cancelled: event stream plus polling.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var events = RunEventsAsync(ct);
        var languageSynced = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
                if (!languageSynced)
                {
                    languageSynced = true;
                    await SyncLanguageAsync(ct);
                }
            }
            catch (ZaprettCallException)
            {
                // unavailable or a broken answer: the state shows it, retry after a pause
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // anything else must not stop the updates for good
                Log?.Invoke("state refresh: " + e);
            }
            var delay = !IsAvailable ? PollUnavailable
                : Job.Str("state") == "running" ? JobPoll
                : EventsActive ? PollWithEvents : PollWithoutEvents;
            try
            {
                var wake = _wake.Task;
                await Task.WhenAny(Task.Delay(delay, ct), wake);
                ct.ThrowIfCancellationRequested();
                if (wake.IsCompleted)
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        try
        {
            await events;
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task RunEventsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var ev in _client.SubscribeAsync(ct))
                {
                    EventsActive = true;
                    try
                    {
                        ApplyEvent(ev);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        Log?.Invoke($"event {ev.Type}: {e}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e) when (e is ZaprettUnavailableException or IOException or NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // no stream now: polling keeps the state fresh
            }
            catch (Exception e)
            {
                Log?.Invoke("event stream: " + e);
            }
            finally
            {
                EventsActive = false;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Waits until the job with this id (or the current one) is not running; reports each state.
    /// Returns the final job object, or null if the state could not be read 5 times in a row.</summary>
    public async Task<JsonObject?> WaitJobAsync(string? jobId, Action<JsonObject>? onUpdate, CancellationToken ct)
    {
        var errors = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject? job;
            try
            {
                job = (await CallAsync("job.status", null, ct)).Obj("job");
                errors = 0;
            }
            catch (ZaprettCallException)
            {
                if (++errors >= 5)
                    return null;
                await Task.Delay(JobPoll, ct);
                continue;
            }
            if (job == null)
                return null;
            if (jobId != null && job.Str("id") != jobId)
                return job.Str("state") == "running" ? null : job;
            Job = job;
            onUpdate?.Invoke(job);
            if (job.Str("state") != "running")
            {
                Changed?.Invoke(this, EventArgs.Empty);
                return job;
            }
            await Task.Delay(JobPoll, ct);
        }
    }

    /// <summary>Starts a background method ({ok, job:{id}}) and waits for it; returns (answer, final job or null).</summary>
    public async Task<(JsonObject Reply, JsonObject? Job)> RunJobAsync(string method, JsonObject? args, Action<JsonObject>? onUpdate, CancellationToken ct = default)
    {
        var reply = await CallAsync(method, args, ct);
        var started = reply.Obj("job");
        if (started == null)
            return (reply, null);
        var first = started.Clone();
        first["state"] ??= "running";
        first["progress"] ??= 0;
        onUpdate?.Invoke(first);
        var job = await WaitJobAsync(started.Str("id"), onUpdate, ct);
        return (reply, job);
    }
}
