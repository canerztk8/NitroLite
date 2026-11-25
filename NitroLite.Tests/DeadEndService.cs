using System.Net;
using System.Net.Sockets;
using NitroLite.Core;

namespace NitroLite.Tests;

/// <summary>
/// Where every fake <see cref="AcerAgentClient"/> points its base class. A fake overrides the reads it needs, but a
/// read it forgot to override would otherwise fall through to the real client and dial 127.0.0.1:46933, which on a
/// machine with Acer's service installed is the real service: the test would then assert against the hardware.
/// <para>
/// This is a loopback listener that accepts and immediately drops, and counts. The fake is therefore isolated by
/// construction (an un-overridden call cannot reach AASSvc), and <see cref="Connections"/> is the sentinel that shows
/// whether any call tried.
/// </para>
/// </summary>
internal static class DeadEndService
{
    private static readonly TcpListener Listener;
    private static int _connections;

    static DeadEndService()
    {
        Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        Port = ((IPEndPoint)Listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public static int Port { get; }

    /// <summary>How many connections fakes have attempted. Zero means every read was answered by an override.</summary>
    public static int Connections => Volatile.Read(ref _connections);

    private static async Task AcceptLoopAsync()
    {
        while (true)
        {
            try
            {
                using var client = await Listener.AcceptTcpClientAsync().ConfigureAwait(false);
                Interlocked.Increment(ref _connections);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
}
