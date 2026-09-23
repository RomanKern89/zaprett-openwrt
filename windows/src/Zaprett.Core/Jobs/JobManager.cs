using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Platform;
using Zaprett.Core.Util;
using Zaprett.Core.Text;

namespace Zaprett.Core.Jobs;

/// <summary>What a running job reports through (router job.uc ctx).</summary>
public sealed class JobContext
{
    readonly JobManager owner;

    internal JobContext(JobManager owner, string id, CancellationToken token)
    {
        this.owner = owner;
        Id = id;
        Token = token;
    }

    public string Id { get; }

    /// <summary>Cancelled by job.cancel (or by the service shutting down).</summary>
    public CancellationToken Token { get; }

    public bool Cancelled => Token.IsCancellationRequested;

    public void Progress(int pct, string? message = null) => owner.Update(Id, pct, message, null);

    public void Log(string message) => owner.AppendLog(message);

    /// <summary>Intermediate result shown by job.status while the job runs.</summary>
    public void Partial(JsonObject result) => owner.Update(Id, null, null, result);

    /// <summary>A context for synchronous calls without progress reporting.</summary>
    public static JobContext Null(JobManager owner) => new(owner, "", CancellationToken.None);
}

/// <summary>Background jobs (router contract §6.3, ARCHITECTURE-WIN §7): one job at a time, state in memory and in
/// run\job.json, log in run\job.log (trimmed at 256 KiB). The core runs a job itself on the thread pool
/// (<see cref="Start"/> → Task.Run); the service only hosts the dispatcher and may await <see cref="WhenIdleAsync"/>
/// before shutting down. Cancellation is cooperative: job.cancel signals the job's CancellationToken and returns at
/// once with state "cancelling"; every handler checks the token between steps and restores what it changed (the
/// automatic selection returns the original strategy). A job.json left "running" by a crashed service is marked
/// failed when the manager starts.</summary>
public sealed class JobManager
{
    public const int LogLimit = 262144;

    public static readonly IReadOnlyList<string> Names =
    [
        "repo-fetch", "repo-install", "repo-remove", "repo-upgrade", "sources-update", "test", "autoupdate", "probe", "dns-setup",
        "diagnose",
    ];

    readonly IPaths paths;
    readonly IClock clock;
    readonly Lock gate = new();
    JsonObject? job;
    CancellationTokenSource? cts;
    Task running = Task.CompletedTask;
    int seq;

    public JobManager(IPaths paths, IClock clock)
    {
        this.paths = paths;
        this.clock = clock;
        job = Files.ReadJson(JobPath);
        if (job != null && R.Str(job["state"]) == "running")
        {
            job["state"] = "failed";
            job["message"] = T.S("job.interrupted");
            job["finished"] = Now;
            job["rc"] = 1;
            Save();
        }
    }

    /// <summary>Raised with the job object on every change (IPC event "job").</summary>
    public event Action<JsonObject>? Changed;

    public string JobPath => Path.Combine(paths.RunDir, "job.json");

    public string LogPath => Path.Combine(paths.RunDir, "job.log");

    long Now => clock.Now.ToUnixTimeSeconds();

    public bool IsBusy
    {
        get
        {
            lock (gate)
                return job != null && R.Str(job["state"]) == "running";
        }
    }

    /// <summary>The current or last job (a copy), null when there was none.</summary>
    public JsonObject? Read()
    {
        lock (gate)
            return job?.DeepClone().AsObject();
    }

    public static JsonObject BusyFail(JsonObject? cur) =>
        R.Fail("job_busy", cur != null ? T.S("job.busy_named", R.Str(cur["name"])) : T.S("job.busy"),
            new JsonObject { ["job"] = cur == null ? null : new JsonObject { ["id"] = cur["id"]?.DeepClone(), ["name"] = cur["name"]?.DeepClone() } });

    JsonObject? Begin(string name, out string id, out CancellationTokenSource source, out TaskCompletionSource finished)
    {
        id = "";
        source = null!;
        finished = null!;
        lock (gate)
        {
            if (job != null && R.Str(job["state"]) == "running")
                return BusyFail(job.DeepClone().AsObject());
            id = $"{Now}-{Interlocked.Increment(ref seq)}";
            // the previous source is not disposed: a late Cancel() of a finished job must not throw
            cts = new CancellationTokenSource();
            source = cts;
            finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            running = finished.Task;
            job = new JsonObject
            {
                ["id"] = id, ["name"] = name, ["state"] = "running", ["progress"] = 0, ["message"] = T.S("job.starting"),
                ["started"] = Now, ["finished"] = 0, ["rc"] = 0, ["result"] = new JsonObject(),
            };
            Directory.CreateDirectory(paths.RunDir);
            Files.AtomicWriteText(LogPath, "");
            Save();
        }
        AppendLog(T.S("job.log_start", name));
        Notify();
        return null;
    }

    /// <summary>Starts a job in the background: {ok, job:{id,name}} at once, or job_busy.</summary>
    public JsonObject Start(string name, Func<JobContext, Task<JsonObject>> handler)
    {
        var busy = Begin(name, out var id, out var source, out var finished);
        if (busy != null)
            return busy;
        var ctx = new JobContext(this, id, source.Token);
        _ = Task.Run(() => Execute(ctx, handler, finished));
        return R.Ok(new JsonObject { ["job"] = new JsonObject { ["id"] = id, ["name"] = name } });
    }

    /// <summary>Runs a job in place (--foreground): the handler's answer, or job_busy.</summary>
    public async Task<JsonObject> RunForegroundAsync(string name, Func<JobContext, Task<JsonObject>> handler)
    {
        var busy = Begin(name, out var id, out var source, out var finished);
        if (busy != null)
            return busy;
        return await Execute(new JobContext(this, id, source.Token), handler, finished).ConfigureAwait(false);
    }

    async Task<JsonObject> Execute(JobContext ctx, Func<JobContext, Task<JsonObject>> handler, TaskCompletionSource finished)
    {
        JsonObject res;
        try
        {
            Update(ctx.Id, null, T.S("job.running"), null);
            res = await handler(ctx).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.Cancelled)
        {
            res = R.Fail("cancelled", T.S("job.cancelled_short"));
        }
        catch (Exception e)
        {
            res = R.Fail("internal_error", T.S("call.internal_error", e.Message));
            AppendLog(R.Message(res)!);
        }
        Finish(ctx, res);
        finished.TrySetResult();
        return res;
    }

    void Finish(JobContext ctx, JsonObject res)
    {
        var state = ctx.Cancelled ? "cancelled" : R.IsOk(res) ? "done" : "failed";
        var msg = R.Message(res);
        if (state == "cancelled")
            msg = T.S("job.cancelled");
        else if (state == "done" && msg == null)
            msg = T.S("job.done");
        AppendLog(msg != null ? T.S("job.log_result_msg", state, msg) : T.S("job.log_result", state));
        lock (gate)
        {
            if (job == null || R.Str(job["id"]) != ctx.Id)
                return;
            job["state"] = state;
            if (state == "done")
                job["progress"] = 100;
            job["message"] = msg;
            job["finished"] = Now;
            job["rc"] = state == "done" ? 0 : 1;
            job["result"] = res.DeepClone();
            Save();
        }
        Notify();
    }

    internal void Update(string id, int? pct, string? message, JsonObject? result)
    {
        if (message != null)
            AppendLog(message);
        lock (gate)
        {
            if (job == null || R.Str(job["id"]) != id)
                return;
            if (pct != null)
                job["progress"] = Math.Clamp(pct.Value, 0, 100);
            if (message != null)
                job["message"] = message;
            if (result != null)
                job["result"] = result.DeepClone();
            Save();
        }
        Notify();
    }

    /// <summary>job.cancel: signals the running job and returns at once ({ok, state:"cancelling"}), or no_job.</summary>
    public JsonObject Cancel()
    {
        CancellationTokenSource? source;
        JsonObject brief;
        lock (gate)
        {
            if (job == null || R.Str(job["state"]) != "running")
                return R.Fail("no_job", T.S("job.no_job"));
            if (job["cancel_requested"] == null)
            {
                job["cancel_requested"] = Now;
                job["message"] = T.S("job.cancelling");
                Save();
            }
            source = cts;
            brief = new JsonObject { ["id"] = job["id"]?.DeepClone(), ["name"] = job["name"]?.DeepClone() };
        }
        // logged before the signal: the job may finish (and log its result) synchronously inside Cancel()
        AppendLog(T.S("job.log_cancel"));
        Notify();
        source?.Cancel();
        return R.Ok(new JsonObject { ["state"] = "cancelling", ["job"] = brief });
    }

    /// <summary>Completes when no job runs (service shutdown, tests).</summary>
    public Task WhenIdleAsync()
    {
        lock (gate)
            return running;
    }

    public void AppendLog(string line)
    {
        var t = clock.Now.ToLocalTime();
        var text = $"[{t:HH:mm:ss}] {line}\n";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(paths.RunDir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length + text.Length > LogLimit)
                {
                    var keep = File.ReadAllText(LogPath, Encoding.UTF8);
                    keep = keep[Math.Max(0, keep.Length - LogLimit / 2)..];
                    var nl = keep.IndexOf('\n', StringComparison.Ordinal);
                    if (nl >= 0)
                        keep = keep[(nl + 1)..];
                    File.WriteAllText(LogPath, T.S("job.log_trimmed") + "\n" + keep, Files.Utf8NoBom);
                }
                File.AppendAllText(LogPath, text, Files.Utf8NoBom);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Last n lines (1..5000) of the job log.</summary>
    public string LogTail(int n)
    {
        n = Math.Clamp(n, 1, 5000);
        string text;
        lock (gate)
            text = Files.ReadLimited(LogPath, LogLimit * 2) ?? "";
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count > n)
            lines = lines.Skip(lines.Count - n).ToList();
        return string.Join('\n', lines);
    }

    void Save()
    {
        if (job != null)
            Files.WriteJson(JobPath, job);
    }

    void Notify()
    {
        var j = Read();
        if (j != null)
            Changed?.Invoke(j);
    }
}
