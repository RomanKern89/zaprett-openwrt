using System.Net;
using System.Net.Sockets;

namespace Zaprett.Service.Tests;

/// <summary>
/// A run of free local TCP ports, held (bound, not listening) until disposed or released. Tests that need exact local
/// ports take them from here instead of fixed numbers: two test runs on the same machine (parallel sessions) collided on
/// 40200-40202 and 40310-40311.
/// </summary>
internal sealed class PortBlock : IDisposable
{
    private const int From = 41000;
    private const int To = 48000;
    private readonly List<Socket> _held;

    public int First { get; }
    public int Last => First + _held.Count - 1;

    private PortBlock(int first, List<Socket> held)
    {
        First = first;
        _held = held;
    }

    /// <summary>Binds <paramref name="count"/> consecutive ports exclusively; another block is tried when one is taken.</summary>
    public static PortBlock Take(int count)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int first = Random.Shared.Next(From, To - count);
            var held = new List<Socket>();
            try
            {
                for (int p = first; p < first + count; p++)
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
                    held.Add(s);
                    s.Bind(new IPEndPoint(IPAddress.Any, p));
                }
                return new PortBlock(first, held);
            }
            catch (SocketException)
            {
                foreach (var s in held)
                    s.Dispose();
            }
        }
        throw new InvalidOperationException($"no {count} free consecutive ports in {From}-{To}");
    }

    /// <summary>Gives the ports back (for a test that binds them itself right after).</summary>
    public void Dispose()
    {
        foreach (var s in _held)
            s.Dispose();
        _held.Clear();
    }
}
