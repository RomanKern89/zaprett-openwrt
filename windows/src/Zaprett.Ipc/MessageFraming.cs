using System.Buffers.Binary;

namespace Zaprett.Ipc;

/// <summary>Malformed frame on the pipe: too large, truncated, or not a valid message.</summary>
public sealed class FramingException(string message) : IOException(message);

/// <summary>
/// Pipe message framing (ARCHITECTURE-WIN §6): uint32 little-endian length, then that many bytes of UTF-8 JSON.
/// A frame is at most 4 MiB; an empty frame is invalid.
/// </summary>
public static class MessageFraming
{
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    private const int HeaderBytes = 4;

    /// <summary>Reads one frame. Returns null on a clean end of stream before the first header byte;
    /// throws <see cref="FramingException"/> on a truncated or oversized frame.</summary>
    public static Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct) => ReadFrameAsync(stream, null, ct);

    /// <summary>As <see cref="ReadFrameAsync(Stream, CancellationToken)"/>; once the header arrived the body must
    /// arrive within <paramref name="bodyTimeout"/> (a slow sender is cut off with OperationCanceledException).</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, TimeSpan? bodyTimeout, CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        int got = await ReadAtLeastAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0)
            return null;
        if (got < HeaderBytes)
            throw new FramingException("truncated frame header");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
            throw new FramingException("empty frame");
        if (length > MaxMessageBytes)
            throw new FramingException($"frame of {length} bytes exceeds the {MaxMessageBytes} byte limit");
        var body = new byte[length];
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (bodyTimeout is { } t)
            bodyCts.CancelAfter(t);
        got = await ReadAtLeastAsync(stream, body, bodyCts.Token).ConfigureAwait(false);
        if (got < body.Length)
            throw new FramingException($"truncated frame: {got} of {length} bytes");
        return body;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length == 0)
            throw new FramingException("empty frame");
        if (payload.Length > MaxMessageBytes)
            throw new FramingException($"frame of {payload.Length} bytes exceeds the {MaxMessageBytes} byte limit");
        // one write per frame: header and body never interleave with another writer's frame
        var buf = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)payload.Length);
        payload.CopyTo(buf.AsMemory(HeaderBytes));
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }
}
