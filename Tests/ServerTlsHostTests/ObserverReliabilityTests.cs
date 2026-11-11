using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connections;
using ServerTransport;

internal static partial class Program
{
    private static async Task ObserverReliabilityTests(X509Certificate2 root, X509Certificate2 certificate)
    {
        using var server = new TlsNetServer(new IPEndPoint(IPAddress.Loopback, 0), certificate, 2, 1, 4096);
        int closed = 0, polls = 0, logs = 0;
        server.OnLogEntry += (_, _) => throw new InvalidOperationException("synthetic diagnostic observer");
        server.OnLogEntry += (_, _) => Interlocked.Increment(ref logs);
        server.OnConnectionClosed += (_, _) => throw new InvalidOperationException("synthetic cleanup observer");
        server.OnConnectionClosed += (_, _) => Interlocked.Increment(ref closed);
        EventHandler badPoll = (_, _) => throw new InvalidOperationException("synthetic maintenance observer");
        server.PollCompleted += badPoll;
        server.PollCompleted += (_, _) => Interlocked.Increment(ref polls);
        server.Start();
        await Wait(() => Volatile.Read(ref polls) >= 2);
        server.PollCompleted -= badPoll;
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.LocalPort, Deadline.Token);
            await using var channel = await TlsPacketChannel.ConnectAsync(socket.GetStream(), "localhost",
                SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()), root, X509RevocationMode.NoCheck, maximum: 4096, cancellation: Deadline.Token);
        }
        await Wait(() => Volatile.Read(ref closed) == 1);
        Check(polls >= 2 && logs >= 2, "Faulty TLS maintenance/log observers blocked other subscribers");
        Check(closed == 1, "Faulty TLS disconnect observer blocked later cleanup");
        server.Stop();
    }
}
