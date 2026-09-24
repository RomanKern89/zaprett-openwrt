using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Zaprett.Core;
using Zaprett.Core.Platform;
using Zaprett.Ipc;

namespace Zaprett.Service.Ipc;

public sealed record PipeServerOptions
{
    public string PipeName { get; init; } = ZaprettPipeClient.DefaultPipeName;
    /// <summary>At most this many connected clients; more wait in the client's connect timeout.</summary>
    public int MaxClients { get; init; } = 16;
    /// <summary>A connection with no request for this long is closed (subscriptions excepted).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Once a frame header arrived, its body must arrive within this time (slow senders).</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>A reply or event must be taken by the client within this time (slow readers).</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Events queued per subscriber; older ones are dropped when a subscriber lags.</summary>
    public int EventQueue { get; init; } = 256;
    /// <summary>Slots only callers with <see cref="CallerInfo.CanModify"/> may take: plain users cannot lock admins out.</summary>
    public int ReservedForModify { get; init; } = 4;
    /// <summary>Connections of one plain (not CanModify) user at a time.</summary>
    public int MaxPerUser { get; init; } = 6;
    /// <summary>Event subscriptions of one plain user at a time.</summary>
    public int MaxSubscriptionsPerUser { get; init; } = 3;
    /// <summary>The first request must arrive within this time after connecting.</summary>
    public TimeSpan FirstRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The pipe may still belong to the previous process for a moment: after a stop that ran past its limit the
    /// SCM is told STOPPED 1.5 s before that process exits (StopSequence.ExitDelay), and a start in between found the
    /// pipe taken. The first instance is tried this many more times, <see cref="FirstInstanceRetryDelay"/> apart.</summary>
    public int FirstInstanceRetries { get; init; } = 5;
    public TimeSpan FirstInstanceRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// \\.\pipe\zaprett server (ARCHITECTURE-WIN §6): DACL from <see cref="CallerResolver.CreatePipeSecurity"/>, JSON-RPC 2.0
/// in length-prefixed frames, one request at a time per connection, caller rights from the client token: methods
/// outside <see cref="ICommandDispatcher.ReadOnlyMethods"/> need <see cref="CallerInfo.CanModify"/>.
/// "subscribe" turns a connection into an event stream.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly Func<NamedPipeServerStream, CallerInfo> _resolveCaller;
    private readonly ILog _log;
    private readonly PipeServerOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Connection, byte> _subscribers = new();
    private int _connected;
    // admission of plain callers (see PipeServerOptions.ReservedForModify / MaxPerUser)
    private readonly object _admission = new();
    private readonly Dictionary<string, (int Connections, int Subscriptions)> _perUser = new(StringComparer.OrdinalIgnoreCase);
    private int _plain;

    public PipeServer(ICommandDispatcher dispatcher, Func<NamedPipeServerStream, CallerInfo> resolveCaller, ILog log, PipeServerOptions? options = null)
    {
        _dispatcher = dispatcher;
        _resolveCaller = resolveCaller;
        _log = log;
        _options = options ?? new PipeServerOptions();
        _slots = new SemaphoreSlim(_options.MaxClients, _options.MaxClients);
    }

    public void Dispose() => _slots.Dispose();

    public int ConnectedClients => Volatile.Read(ref _connected);
    public int Subscribers => _subscribers.Count;

    /// <summary>Accept loop; returns when cancelled. The first instance is created with FirstPipeInstance, so the
    /// server fails instead of joining a pipe somebody else already owns.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        bool first = true;
        int taken = 0;
        var handlers = new ConcurrentDictionary<Task, byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                NamedPipeServerStream pipe;
                try
                {
                    pipe = Create(first);
                    first = false;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _slots.Release();
                    if (first && taken < _options.FirstInstanceRetries)
                    {
                        taken++;
                        _log.Info($"ipc: pipe {_options.PipeName} is taken ({e.Message}), try {taken} of {_options.FirstInstanceRetries} " +
                                  $"in {_options.FirstInstanceRetryDelay.TotalSeconds:0.#} s");
                        await Task.Delay(_options.FirstInstanceRetryDelay, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (first)
                        throw new InvalidOperationException($@"cannot create \\.\pipe\{_options.PipeName}: {e.Message}", e);
                    _log.Warn("ipc: cannot create a pipe instance: " + e.Message);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    _slots.Release();
                    if (ct.IsCancellationRequested)
                        break;
                    continue;
                }
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await HandleAsync(pipe, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _slots.Release();
                    }
                }, CancellationToken.None);
                handlers[task] = 0;
                _ = task.ContinueWith(t => handlers.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        await Task.WhenAll(handlers.Keys).ConfigureAwait(false);
    }

    private NamedPipeServerStream Create(bool first) =>
        NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, _options.MaxClients, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 65536, 65536,
            CallerResolver.CreatePipeSecurity());

    /// <summary>Sends an event to every subscriber (dispatcher and engine events).</summary>
    public void Publish(string type, JsonObject data)
    {
        foreach (var c in _subscribers.Keys)
        {
            var note = JsonRpc.Notification(JsonRpc.EventMethod, new JsonObject { ["type"] = type, ["data"] = data.DeepClone() });
            c.Events.Writer.TryWrite(note);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken serverCt)
    {
        Interlocked.Increment(ref _connected);
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        using var conn = new Connection(pipe, _options.EventQueue);
        Task? eventPump = null;
        CallerInfo? admitted = null;
        try
        {
            var caller = _resolveCaller(pipe);
            if (!Admit(caller))
            {
                _log.Warn($"ipc: {caller.UserName}: too many connections, refused");
                return;
            }
            admitted = caller;
            bool first = true;
            while (!connCts.IsCancellationRequested)
            {
                byte[]? frame;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token))
                {
                    if (first)
                        idle.CancelAfter(_options.FirstRequestTimeout);
                    else if (!conn.Subscribed)
                        idle.CancelAfter(_options.IdleTimeout);
                    first = false;
                    frame = await MessageFraming.ReadFrameAsync(pipe, _options.FrameTimeout, idle.Token).ConfigureAwait(false);
                }
                if (frame is null)
                    break;
                JsonObject reply;
                bool subscribe = false;
                try
                {
                    var req = JsonRpc.ParseRequest(frame);
                    if (req.Method == JsonRpc.SubscribeMethod && !conn.Subscribed && !AdmitSubscription(caller))
                    {
                        reply = JsonRpc.Result(req.Id, ServiceMessages.Fail("too_many_subscriptions", req.Params));
                    }
                    else if (req.Method == JsonRpc.SubscribeMethod)
                    {
                        conn.SubscriptionCounted = true;
                        subscribe = true;
                        reply = JsonRpc.Result(req.Id, new JsonObject { ["ok"] = true });
                    }
                    else
                    {
                        reply = JsonRpc.Result(req.Id, await InvokeAsync(req, caller, connCts.Token).ConfigureAwait(false));
                    }
                }
                catch (JsonRpcException e)
                {
                    reply = JsonRpc.Error(e.Id, e.Code, e.Message);
                }
                await conn.WriteAsync(reply, _options.WriteTimeout, connCts.Token).ConfigureAwait(false);
                if (subscribe && !conn.Subscribed)
                {
                    conn.Subscribed = true;
                    _subscribers[conn] = 0;
                    eventPump = PumpEventsAsync(conn, connCts);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // idle timeout, slow client or shutdown
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            if (e is FramingException)
                _log.Warn("ipc: dropped a client: " + e.Message);
        }
        catch (Exception e)
        {
            _log.Error("ipc: connection failed: " + e);
        }
        finally
        {
            _subscribers.TryRemove(conn, out _);
            conn.Events.Writer.TryComplete();
            await connCts.CancelAsync().ConfigureAwait(false);
            if (eventPump is not null)
            {
                try
                {
                    await eventPump.ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log.Warn("ipc: event pump: " + e.Message);
                }
            }
            if (admitted is not null)
                Release(admitted, conn.SubscriptionCounted);
            try
            {
                if (pipe.IsConnected)
                    pipe.Disconnect();
            }
            catch (IOException)
            {
            }
            await pipe.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _connected);
        }
    }

    private bool Admit(CallerInfo caller)
    {
        if (caller.CanModify)
            return true;
        lock (_admission)
        {
            _perUser.TryGetValue(caller.UserName, out var u);
            if (_plain >= _options.MaxClients - _options.ReservedForModify || u.Connections >= _options.MaxPerUser)
                return false;
            _plain++;
            _perUser[caller.UserName] = (u.Connections + 1, u.Subscriptions);
            return true;
        }
    }

    private bool AdmitSubscription(CallerInfo caller)
    {
        if (caller.CanModify)
            return true;
        lock (_admission)
        {
            _perUser.TryGetValue(caller.UserName, out var u);
            if (u.Subscriptions >= _options.MaxSubscriptionsPerUser)
                return false;
            _perUser[caller.UserName] = (u.Connections, u.Subscriptions + 1);
            return true;
        }
    }

    private void Release(CallerInfo caller, bool subscribed)
    {
        if (caller.CanModify)
            return;
        lock (_admission)
        {
            _plain--;
            _perUser.TryGetValue(caller.UserName, out var u);
            u = (u.Connections - 1, u.Subscriptions - (subscribed ? 1 : 0));
            if (u.Connections <= 0)
                _perUser.Remove(caller.UserName);
            else
                _perUser[caller.UserName] = u;
        }
    }

    private async Task<JsonObject> InvokeAsync(RpcRequest req, CallerInfo caller, CancellationToken ct)
    {
        if (!_dispatcher.ReadOnlyMethods.Contains(req.Method) && !caller.CanModify)
        {
            _log.Warn($"ipc: {caller.UserName}: {req.Method} denied");
            return ServiceMessages.Fail("access_denied", req.Params);
        }
        try
        {
            return await _dispatcher.InvokeAsync(req.Method, req.Params, caller, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Error($"ipc: {req.Method} failed: {e}");
            throw new JsonRpcException(JsonRpc.InternalError, "internal error in " + req.Method, req.Id);
        }
    }

    private async Task PumpEventsAsync(Connection conn, CancellationTokenSource connCts)
    {
        try
        {
            await foreach (var note in conn.Events.Reader.ReadAllAsync(connCts.Token).ConfigureAwait(false))
                await conn.WriteAsync(note, _options.WriteTimeout, connCts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // a subscriber that does not read its events is disconnected
            await connCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private sealed class Connection(NamedPipeServerStream pipe, int eventQueue) : IDisposable
    {
        public void Dispose() => _write.Dispose();

        private readonly SemaphoreSlim _write = new(1, 1);

        public bool Subscribed { get; set; }
        public bool SubscriptionCounted { get; set; }

        public Channel<JsonObject> Events { get; } = Channel.CreateBounded<JsonObject>(
            new BoundedChannelOptions(eventQueue) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public async Task WriteAsync(JsonObject message, TimeSpan timeout, CancellationToken ct)
        {
            await _write.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                byte[] bytes = JsonRpc.Serialize(message);
                if (bytes.Length > MessageFraming.MaxMessageBytes)
                    bytes = JsonRpc.Serialize(JsonRpc.Error(message["id"], JsonRpc.InternalError, "answer is larger than 4 MiB"));
                await MessageFraming.WriteFrameAsync(pipe, bytes, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }
    }
}
