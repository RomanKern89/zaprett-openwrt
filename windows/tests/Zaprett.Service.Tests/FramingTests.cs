using System.Buffers.Binary;
using System.Text;
using Zaprett.Ipc;

namespace Zaprett.Service.Tests;

public class FramingTests
{
    private static byte[] Header(uint length)
    {
        var h = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(h, length);
        return h;
    }

    [Fact]
    public async Task RoundTrip_SeveralFrames_ThenCleanEnd()
    {
        var ms = new MemoryStream();
        await MessageFraming.WriteFrameAsync(ms, Encoding.UTF8.GetBytes("{\"a\":1}"), default);
        await MessageFraming.WriteFrameAsync(ms, Encoding.UTF8.GetBytes("{\"b\":\"привет\"}"), default);
        ms.Position = 0;
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await MessageFraming.ReadFrameAsync(ms, default))!));
        Assert.Equal("{\"b\":\"привет\"}", Encoding.UTF8.GetString((await MessageFraming.ReadFrameAsync(ms, default))!));
        Assert.Null(await MessageFraming.ReadFrameAsync(ms, default));
    }

    [Fact]
    public async Task Header_IsLittleEndianLength()
    {
        var ms = new MemoryStream();
        await MessageFraming.WriteFrameAsync(ms, new byte[] { 1, 2, 3 }, default);
        Assert.Equal(new byte[] { 3, 0, 0, 0, 1, 2, 3 }, ms.ToArray());
    }

    [Fact]
    public async Task MaxSizeFrame_IsAccepted()
    {
        var ms = new MemoryStream();
        var payload = new byte[MessageFraming.MaxMessageBytes];
        await MessageFraming.WriteFrameAsync(ms, payload, default);
        ms.Position = 0;
        Assert.Equal(MessageFraming.MaxMessageBytes, (await MessageFraming.ReadFrameAsync(ms, default))!.Length);
    }

    [Fact]
    public async Task TruncatedHeader_Throws()
    {
        var ms = new MemoryStream(new byte[] { 5, 0 });
        await Assert.ThrowsAsync<FramingException>(() => MessageFraming.ReadFrameAsync(ms, default));
    }

    [Fact]
    public async Task TruncatedBody_Throws()
    {
        var ms = new MemoryStream([.. Header(10), 1, 2, 3]);
        var e = await Assert.ThrowsAsync<FramingException>(() => MessageFraming.ReadFrameAsync(ms, default));
        Assert.Contains("3 of 10", e.Message);
    }

    [Fact]
    public async Task OversizedHeader_ThrowsWithoutReadingBody()
    {
        var ms = new MemoryStream([.. Header(MessageFraming.MaxMessageBytes + 1), 0, 0]);
        await Assert.ThrowsAsync<FramingException>(() => MessageFraming.ReadFrameAsync(ms, default));
        Assert.Equal(4, ms.Position);
    }

    [Fact]
    public async Task GarbageHeader_IsRejectedAsTooLarge()
    {
        // ASCII text where a header should be: "GET " = 0x20544547 > 4 MiB
        var ms = new MemoryStream(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
        await Assert.ThrowsAsync<FramingException>(() => MessageFraming.ReadFrameAsync(ms, default));
    }

    [Fact]
    public async Task EmptyFrame_IsRejected()
    {
        await Assert.ThrowsAsync<FramingException>(() => MessageFraming.ReadFrameAsync(new MemoryStream(Header(0)), default));
        await Assert.ThrowsAsync<FramingException>(() => MessageFraming.WriteFrameAsync(new MemoryStream(), Array.Empty<byte>(), default));
    }

    [Fact]
    public async Task WriteOversized_IsRejected()
    {
        var ms = new MemoryStream();
        await Assert.ThrowsAsync<FramingException>(() =>
            MessageFraming.WriteFrameAsync(ms, new byte[MessageFraming.MaxMessageBytes + 1], default));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task SlowBody_IsCutOffByBodyTimeout()
    {
        string name = "zaprett-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.In, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 4096, 4096);   // buffer 0 would block the writer
        using var writer = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous);
        await Task.WhenAll(pipe.WaitForConnectionAsync(), writer.ConnectAsync(5000));
        writer.Write([.. Header(100), 1, 2]);
        writer.Flush();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MessageFraming.ReadFrameAsync(pipe, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.InRange(sw.ElapsedMilliseconds, 200, 5000);
    }
}
