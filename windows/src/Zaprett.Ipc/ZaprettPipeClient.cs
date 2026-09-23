using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Zaprett.Ipc;

/// <summary>
/// <see cref="IZaprettClient"/> over \\.\pipe\zaprett. Calls share one connection and run one at a time;
/// every <see cref="SubscribeAsync"/> opens its own connection that only carries events.
/// </summary>
public sealed class ZaprettPipeClient : IZaprettClient
{
    public const string DefaultPipeName = "zaprett";

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _callTimeout;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private long _nextId;
    private bool _disposed;

    /// <summary>\\.\pipe\zaprett with default timeouts (the UI creates the client by type name with this constructor).</summary>
    public ZaprettPipeClient() : this(DefaultPipeName)
    {
    }

    public ZaprettPipeClient(string pipeName, TimeSpan? connectTimeout = null, TimeSpan? callTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(120);
    }

    public async Task<JsonObject> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!JsonRpc.IsValidMethodName(method))
            throw new ArgumentException("invalid method name: " + method, nameof(method));
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_callTimeout);
            try
            {
                _pipe ??= await ConnectAsync(cts.Token).ConfigureAwait(false);
                long id = ++_nextId;
                await MessageFraming.WriteFrameAsync(_pipe, JsonRpc.Serialize(JsonRpc.Request(id, method, args)), cts.Token)
                    .ConfigureAwait(false);
                while (true)
                {
                    var frame = await MessageFraming.ReadFrameAsync(_pipe, cts.Token).ConfigureAwait(false)
                        ?? throw new IOException("the service closed the connection");
                    var msg = JsonRpc.ParseIncoming(frame);
                    if (msg.IsNotification || msg.Id is not JsonValue v || !v.TryGetValue<long>(out var rid) || rid != id)
                        continue;
                    if (msg.Result is not null)
                        return msg.Result;
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["error"] = "rpc_error",
                        ["code"] = msg.ErrorCode,
                        ["message"] = msg.ErrorMessage,
                    };
                }
            }
            catch (Exception e) when (IsTransportFailure(e, ct))
            {
                DropConnection();
                throw new ZaprettUnavailableException("zaprett service is not available: " + e.Message, e);
            }
            catch
            {
                // a cancelled or failed exchange leaves the stream mid-frame: never reuse it
                DropConnection();
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async IAsyncEnumerable<ServiceEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = await ConnectAsync(ct).ConfigureAwait(false);
            await MessageFraming.WriteFrameAsync(pipe, JsonRpc.Serialize(JsonRpc.Request(1, JsonRpc.SubscribeMethod, null)), ct)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (pipe is not null)
                await pipe.DisposeAsync().ConfigureAwait(false);
            if (IsTransportFailure(e, ct))
                throw new ZaprettUnavailableException("zaprett service is not available: " + e.Message, e);
            throw;
        }
        await using (pipe.ConfigureAwait(false))
        {
            while (!ct.IsCancellationRequested)
            {
                RpcIncoming msg;
                try
                {
                    var frame = await MessageFraming.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
                    if (frame is null)
                        yield break;
                    msg = JsonRpc.ParseIncoming(frame);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }
                catch (Exception e) when (e is IOException or JsonRpcException)
                {
                    yield break;
                }
                if (!msg.IsNotification)
                {
                    if (msg.ErrorCode is not null || (msg.Result?["ok"] is JsonValue okv && okv.TryGetValue<bool>(out var ok) && !ok))
                        throw new ZaprettUnavailableException("subscribe refused: " + (msg.ErrorMessage ?? msg.Result?.ToJsonString()));
                    continue;
                }
                if (msg.NotificationMethod != JsonRpc.EventMethod || msg.NotificationParams is null)
                    continue;
                string type = msg.NotificationParams["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "";
                var data = msg.NotificationParams["data"] as JsonObject ?? [];
                msg.NotificationParams.Remove("data");
                yield return new ServiceEvent(type, data);
            }
        }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        // Identification is enough for the service to read our token; it cannot act as us.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
                PipeServerCheck.Verify(pipe);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsTransportFailure(Exception e, CancellationToken callerToken) => e switch
    {
        TimeoutException or IOException or UnauthorizedAccessException or JsonRpcException => true,
        // our own call timeout, not the caller's cancellation
        OperationCanceledException => !callerToken.IsCancellationRequested,
        _ => false,
    };

    private void DropConnection()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            DropConnection();
            _lock.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
