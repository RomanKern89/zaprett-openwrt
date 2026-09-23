using System.Text;
using Zaprett.Core.Platform;
using Zaprett.Service.Native;

namespace Zaprett.Service.Platform;

/// <summary><see cref="IProcessRunner"/>: argv without a shell, every run in its own job; a timeout or cancellation
/// kills the whole process tree.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Output kept per stream; the rest is read and dropped so the child never blocks on a full pipe.</summary>
    public const int MaxOutputBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = Win32Process.Start(fileName, args);
        var outTask = ReadCappedAsync(proc.StdOut);
        var errTask = ReadCappedAsync(proc.StdErr);
        bool timedOut = false;
        int exitCode;
        try
        {
            exitCode = await proc.Exited.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
            exitCode = -1;
            proc.Kill();
        }
        catch (OperationCanceledException)
        {
            proc.Kill();
            throw;
        }
        // grandchildren may still hold the pipes: give them a moment, then end the whole job
        var drained = Task.WhenAll(outTask, errTask);
        if (await Task.WhenAny(drained, Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false) != drained)
            proc.Kill();
        await drained.ConfigureAwait(false);
        return new ProcessResult(exitCode, Decode(outTask.Result), Decode(errTask.Result), timedOut);
    }

    private static async Task<byte[]> ReadCappedAsync(Stream s)
    {
        var ms = new MemoryStream();
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                int keep = (int)Math.Min(n, MaxOutputBytes - ms.Length);
                if (keep > 0)
                    ms.Write(buf, 0, keep);
            }
        }
        catch (IOException)
        {
            // broken pipe = the writer is gone
        }
        catch (ObjectDisposedException)
        {
        }
        return ms.ToArray();
    }

    private static readonly Lazy<Encoding> Oem = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding((int)Win32.GetOEMCP());
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    });

    /// <summary>Console programs write UTF-8 or the OEM code page (netsh, PowerShell on ru-RU: cp866).</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Oem.Value.GetString(bytes);
        }
    }
}
