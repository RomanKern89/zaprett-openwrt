using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Zaprett.Core;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Hosting;

/// <summary>A dispatcher call the scheduler wants to make.</summary>
public sealed record ScheduledCall(string Method, JsonObject? Args);

/// <summary>
/// What is due, as the router's cron lines (cron.uc wanted()): watchdog "ensure" every 5 minutes when main.enabled and
/// main.watchdog; "monitor.run" every monitor.interval minutes when main.enabled and monitor.enabled; once a day at
/// repo.autoupdate_hour "autoupdate" (the core's daily job) when repo.autoupdate is on or any subscription is enabled.
/// Pure: time and configuration come in, the caller performs the calls.
/// </summary>
public sealed class Schedule
{
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _lastWatchdog;
    private DateTimeOffset? _lastMonitor;
    private DateOnly? _lastAutoupdate;

    public IReadOnlyList<ScheduledCall> Due(DateTimeOffset now, JsonObject? config)
    {
        var calls = new List<ScheduledCall>();
        var main = config?["main"] as JsonObject;
        bool enabled = Bool(main?["enabled"], false);

        if (enabled && Bool(main?["watchdog"], true) && (_lastWatchdog is null || now - _lastWatchdog >= WatchdogInterval))
        {
            calls.Add(new ScheduledCall("ensure", null));
            _lastWatchdog = now;
        }

        var monitor = config?["monitor"] as JsonObject;
        int interval = Math.Clamp(Int(monitor?["interval"], 30), 1, 1440);
        if (enabled && Bool(monitor?["enabled"], true))
        {
            if (_lastMonitor is null)
                _lastMonitor = now;   // first check one interval after start, like cron
            else if (now - _lastMonitor >= TimeSpan.FromMinutes(interval))
            {
                calls.Add(new ScheduledCall("monitor.run", null));
                _lastMonitor = now;
            }
        }

        var repo = config?["repo"] as JsonObject;
        int hour = Math.Clamp(Int(repo?["autoupdate_hour"], 4), 0, 23);
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (AutoupdateNeeded(repo, config?["sources"] as JsonObject) && now.LocalDateTime.Hour == hour && _lastAutoupdate != today)
        {
            calls.Add(new ScheduledCall("autoupdate", null));
            _lastAutoupdate = today;
        }
        return calls;
    }

    private static bool AutoupdateNeeded(JsonObject? repo, JsonObject? sources)
    {
        if (Bool(repo?["autoupdate"], true))
            return true;
        return sources?.Any(s => s.Value is JsonObject o && Bool(o["enabled"], false)) ?? false;
    }

    private static bool Bool(JsonNode? n, bool dflt) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : dflt;
    private static int Int(JsonNode? n, int dflt) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : dflt;
}

/// <summary>Runs <see cref="Schedule"/> every 30 seconds against config.json and calls the dispatcher as SYSTEM.</summary>
public sealed class SchedulerService(ICommandDispatcher dispatcher, PlatformServices platform, StartupGate gate) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupWaitLimit = TimeSpan.FromMinutes(10);
    private readonly Schedule _schedule = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // no watchdog while the core is still starting the engine (StartupGate)
        try
        {
            await gate.Done.WaitAsync(StartupWaitLimit, stoppingToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // a start that hangs must not switch the watchdog off for good
            platform.Log.Warn($"scheduler: the core start takes longer than {StartupWaitLimit.TotalMinutes:0} min, starting the watchdog anyway");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var call in _schedule.Due(platform.Clock.Now, ReadConfig()))
            {
                try
                {
                    var res = await dispatcher.InvokeAsync(call.Method, call.Args, CallerInfo.System, stoppingToken).ConfigureAwait(false);
                    if (res["ok"]?.GetValue<bool>() != true)
                        platform.Log.Warn($"scheduler: {call.Method}: {res.ToJsonString()}");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    platform.Log.Error($"scheduler: {call.Method} failed: {e.Message}");
                }
            }
            try
            {
                await platform.Clock.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private JsonObject? ReadConfig()
    {
        try
        {
            return File.Exists(platform.Paths.ConfigFile)
                ? JsonNode.Parse(File.ReadAllText(platform.Paths.ConfigFile)) as JsonObject
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            platform.Log.Warn("scheduler: config.json: " + e.Message);
            return null;
        }
    }
}
