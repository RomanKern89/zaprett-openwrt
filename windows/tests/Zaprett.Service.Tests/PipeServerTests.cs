using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core;
using Zaprett.Ipc;
using Zaprett.Service.Ipc;

namespace Zaprett.Service.Tests;

/// <summary>Server and client over a real named pipe (unique name per test).</summary>
public sealed class PipeServerTests : IAsyncLifetime
{
    private static readonly CallerInfo Admin = new("admin", true, false);
    private static readonly CallerInfo User = new("user", false, false);
    private static readonly CallerInfo Operator = new("op", false, true);

    private readonly string _pipe = "zaprett-test-" + Guid.NewGuid().ToString("N");
    private readonly FakeDispatcher _dispatcher = new();
    private readonly MemoryLog _log = new();
    private readonly CancellationTokenSource _cts = new();
    private PipeServer _server = null!;
    private Task _run = null!;
    private Func<NamedPipeServerStream, CallerInfo> _caller = _ => User;

    private void Start(PipeServerOptions? options = null)
    {
        _server = new PipeServer(_dispatcher, p => _caller(p), _log, (options ?? new PipeServerOptions()) with { PipeName = _pipe });
        _dispatcher.Event += _server.Publish;
        _run = _server.RunAsync(_cts.Token);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_run is not null)
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        _server?.Dispose();
    }

    private ZaprettPipeClient Client(int connectMs = 3000) => new(_pipe, TimeSpan.FromMilliseconds(connectMs));

    [Fact]
    public async Task ReadOnlyMethod_WorksForAnyCaller()
    {
        Start();
        await using var c = Client();
        var r = await c.CallAsync("status");
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal("status", r["method"]!.GetValue<string>());
        Assert.Equal("user", _dispatcher.Calls.Single().Caller.UserName);
    }

    [Fact]
    public async Task ModifyingMethod_IsDeniedForPlainUser_AndNeverReachesTheCore()
    {
        Start();
        await using var c = Client();
        var r = await c.CallAsync("start");
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("access_denied", r["error"]!.GetValue<string>());
        Assert.Empty(_dispatcher.Calls);
    }

    [Theory]
    [InlineData(null, "Для этого действия")]
    [InlineData("en", "This action needs")]
    [InlineData("zh-CN", "此操作需要")]
    public async Task AccessDenied_IsInTheRequestLanguage_RussianByDefault(string? lang, string start)
    {
        Start();
        await using var c = Client();
        var r = await c.CallAsync("start", lang is null ? null : new JsonObject { ["lang"] = lang });
        Assert.StartsWith(start, r["message"]!.GetValue<string>());
    }

    [Fact]
    public void ServiceMessages_ExistInAllLanguages()
    {
        foreach (var code in ServiceMessages.Codes)
            Assert.Equal(3, new[] { "ru", "en", "zh-CN" }.Select(l => ServiceMessages.Text(code, l)).Distinct().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModifyingMethod_IsAllowedForAdminAndOperator(bool admin)
    {
        _caller = _ => admin ? Admin : Operator;
        Start();
        await using var c = Client();
        var r = await c.CallAsync("list.enable", new JsonObject { ["id"] = "zaprett-youtube" });
        Assert.True(r["ok"]!.GetValue<bool>());
        var call = _dispatcher.Calls.Single();
        Assert.Equal("list.enable", call.Method);
        Assert.Equal("zaprett-youtube", call.Args!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task RealTokenResolver_MatchesTheCurrentUsersElevation()
    {
        _caller = CallerResolver.FromPipe;
        Start();
        await using var c = Client();
        await c.CallAsync("status");
        var caller = _dispatcher.Calls.Single().Caller;
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(me.Name, caller.UserName);
        bool elevatedAdmin = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        Assert.Equal(elevatedAdmin, caller.IsAdmin);
        // this workstation runs the tests unelevated: the real resolver must then refuse changes
        if (!elevatedAdmin && CallerResolver.OperatorsSid() is null)
        {
            var r = await c.CallAsync("start");
            Assert.Equal("access_denied", r["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task CoreException_BecomesRpcError_ConnectionSurvives()
    {
        _dispatcher.Handler = (m, _) => m == "status" ? throw new InvalidOperationException("boom") : Task.FromResult(new JsonObject { ["ok"] = true });
        Start();
        await using var c = Client();
        var r = await c.CallAsync("status");
        Assert.Equal("rpc_error", r["error"]!.GetValue<string>());
        Assert.Equal(JsonRpc.InternalError, r["code"]!.GetValue<int>());
        Assert.DoesNotContain("boom", r.ToJsonString());   // no internals to clients
        Assert.True((await c.CallAsync("version"))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Subscribe_ReceivesEvents()
    {
        Start();
        await using var c = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<ServiceEvent>();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in c.SubscribeAsync(cts.Token))
            {
                received.Add(e);
                if (received.Count == 2)
                    break;
            }
        });
        Assert.True(await Wait.UntilAsync(() => _server.Subscribers == 1, TimeSpan.FromSeconds(5)));
        _dispatcher.Raise("job", new JsonObject { ["state"] = "running" });
        _dispatcher.Raise("status", new JsonObject { ["running"] = true });
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["job", "status"], received.Select(e => e.Type));
        Assert.Equal("running", received[0].Data["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task GarbageFrame_DropsOnlyThatClient()
    {
        Start();
        using (var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(3000);
            await raw.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
            var buf = new byte[16];
            int n = await raw.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, n);   // server closed the connection
        }
        Assert.Contains(_log.Lines, l => l.Contains("dropped a client"));
        await using var c = Client();
        Assert.True((await c.CallAsync("status"))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task InvalidJson_GetsParseErrorAndConnectionStays()
    {
        Start();
        using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(3000);
        await MessageFraming.WriteFrameAsync(raw, Encoding.UTF8.GetBytes("{oops"), default);
        var reply = JsonRpc.ParseIncoming((await MessageFraming.ReadFrameAsync(raw, default))!);
        Assert.Equal(JsonRpc.ParseError, reply.ErrorCode);
        await MessageFraming.WriteFrameAsync(raw, JsonRpc.Serialize(JsonRpc.Request(5, "status", null)), default);
        var ok = JsonRpc.ParseIncoming((await MessageFraming.ReadFrameAsync(raw, default))!);
        Assert.True(ok.Result!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SlowSender_IsCutOff()
    {
        Start(new PipeServerOptions { FrameTimeout = TimeSpan.FromMilliseconds(300) });
        using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(3000);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1000);
        await raw.WriteAsync(header);
        await raw.WriteAsync(new byte[10]);
        int n = await raw.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task IdleClient_IsDisconnected()
    {
        Start(new PipeServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) });
        using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(3000);
        // one request, then silence
        await MessageFraming.WriteFrameAsync(raw, JsonRpc.Serialize(JsonRpc.Request(1, "status", null)), default);
        Assert.NotNull(await MessageFraming.ReadFrameAsync(raw, default));
        Assert.True(await Wait.UntilAsync(() => _server.ConnectedClients == 1, TimeSpan.FromSeconds(3)));
        int n = await raw.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, n);
        Assert.True(await Wait.UntilAsync(() => _server.ConnectedClients == 0, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task OversizedFrame_IsRefused()
    {
        Start();
        using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(3000);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, MessageFraming.MaxMessageBytes + 1);
        await raw.WriteAsync(header);
        int n = await raw.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task ClientLimit_SeventeenthWaitsUntilASlotFrees()
    {
        _caller = _ => Admin;
        Start(new PipeServerOptions { MaxClients = 16 });
        var clients = new List<ZaprettPipeClient>();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                var c = Client();
                await c.CallAsync("status");
                clients.Add(c);
            }
            Assert.Equal(16, _server.ConnectedClients);
            await using (var extra = Client(connectMs: 500))
                await Assert.ThrowsAsync<ZaprettUnavailableException>(() => extra.CallAsync("status"));

            await clients[0].DisposeAsync();
            clients.RemoveAt(0);
            await using var late = Client(connectMs: 5000);
            Assert.True((await late.CallAsync("status"))["ok"]!.GetValue<bool>());
        }
        finally
        {
            foreach (var c in clients)
                await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlainUser_IsLimitedPerUser()
    {
        Start(new PipeServerOptions { MaxPerUser = 3 });
        var clients = new List<ZaprettPipeClient>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var c = Client();
                Assert.True((await c.CallAsync("status"))["ok"]!.GetValue<bool>());
                clients.Add(c);
            }
            await using (var fourth = Client())
                await Assert.ThrowsAsync<ZaprettUnavailableException>(() => fourth.CallAsync("status"));
            Assert.Contains(_log.Lines, l => l.Contains("too many connections"));
            await clients[0].DisposeAsync();
            clients.RemoveAt(0);
            Assert.True(await Wait.UntilAsync(() => _server.ConnectedClients == 2, TimeSpan.FromSeconds(5)));
            await using var again = Client();
            Assert.True((await again.CallAsync("status"))["ok"]!.GetValue<bool>());
        }
        finally
        {
            foreach (var c in clients)
                await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlainUsers_CannotTakeTheReservedSlots()
    {
        int n = 0;
        // 12 different plain users fill every unreserved slot, then an admin arrives
        _caller = _ => Interlocked.Increment(ref n) <= 13 ? new CallerInfo("user" + n, false, false) : Admin;
        Start(new PipeServerOptions { MaxClients = 16, ReservedForModify = 4, MaxPerUser = 6 });
        var clients = new List<ZaprettPipeClient>();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                var c = Client();
                await c.CallAsync("status");
                clients.Add(c);
            }
            await using (var thirteenth = Client())
                await Assert.ThrowsAsync<ZaprettUnavailableException>(() => thirteenth.CallAsync("status"));
            await using var admin = Client();
            var r = await admin.CallAsync("start");
            Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        }
        finally
        {
            foreach (var c in clients)
                await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlainUser_SubscriptionsAreLimited()
    {
        Start(new PipeServerOptions { MaxSubscriptionsPerUser = 1 });
        await using var c1 = Client();
        await using var c2 = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = c1.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = first.MoveNextAsync().AsTask();
        Assert.True(await Wait.UntilAsync(() => _server.Subscribers == 1, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ZaprettUnavailableException>(async () =>
        {
            await foreach (var _ in c2.SubscribeAsync(cts.Token))
            {
            }
        });
        Assert.Equal(1, _server.Subscribers);
        await cts.CancelAsync();
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }
        await first.DisposeAsync();
    }

    [Fact]
    public async Task SilentClient_MustSendTheFirstRequestQuickly()
    {
        Start(new PipeServerOptions { FirstRequestTimeout = TimeSpan.FromMilliseconds(300), IdleTimeout = TimeSpan.FromMinutes(5) });
        using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(3000);
        int n = await raw.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, n);
    }

    [Fact]
    public void ServerTrust_ServiceOrSameUserOnly()
    {
        var me = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var other = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        Assert.True(PipeServerCheck.IsTrusted(100, 100, null, user));        // the service process
        Assert.True(PipeServerCheck.IsTrusted(100, null, user, user));       // console mode, same user
        Assert.False(PipeServerCheck.IsTrusted(100, 200, other, user));     // squatter while the service runs
        Assert.False(PipeServerCheck.IsTrusted(100, null, other, user));    // squatter, no service
        Assert.False(PipeServerCheck.IsTrusted(100, null, null, user));     // unknown owner
        Assert.False(PipeServerCheck.IsTrusted(0, 0, null, me));
    }

    [Fact]
    public async Task NoServer_IsUnavailable()
    {
        await using var c = new ZaprettPipeClient("zaprett-none-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<ZaprettUnavailableException>(() => c.CallAsync("status"));
    }

    [Fact]
    public async Task SecondServerOnSamePipe_Fails()
    {
        Start();
        await using (var c = Client())
            await c.CallAsync("status");
        using var other = new PipeServer(new FakeDispatcher(), _ => User, _log, new PipeServerOptions { PipeName = _pipe });
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void PipeDacl_InteractiveUsersCannotCreateInstances()
    {
        var ps = CallerResolver.CreatePipeSecurity();
        var rules = ps.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var interactive = rules.Single(r => r.IdentityReference.Value == "S-1-5-4");
        Assert.True(interactive.PipeAccessRights.HasFlag(PipeAccessRights.ReadData));
        Assert.True(interactive.PipeAccessRights.HasFlag(PipeAccessRights.WriteData));
        Assert.False(interactive.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(interactive.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-18" && r.PipeAccessRights == PipeAccessRights.FullControl);
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-32-544" && r.PipeAccessRights == PipeAccessRights.FullControl);
        // no Everyone, no Users, no Anonymous
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-32-545" or "S-1-5-7");
    }
}
