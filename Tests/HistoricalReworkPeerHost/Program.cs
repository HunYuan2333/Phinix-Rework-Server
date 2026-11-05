using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Authentication;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LiteNetLib;
using UserManagement;
using Utils;

// Test composition host using real current components; not the published Server or adapter oracle.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        try
        {
            using var net = new NetServer(new IPEndPoint(IPAddress.Any, 0), 2);
            var auth = new ServerAuthenticator(net, "Synthetic peer", "Historical module probe", AuthTypes.ClientKey);
            try
            {
                var users = new ServerUserManager(net, auth);
                var fixtures = new List<object>();
                var gate = new object();
                int errors = 0;
                var handlers = (Dictionary<string, NetCommon.PacketHandlerDelegate>)typeof(NetCommon).GetField("registeredPacketHandlers", Private).GetValue(net);
                var original = handlers["UserManagement"];
                // Install before starting the poller; no concurrent handler-dictionary mutation.
                handlers["UserManagement"] = (module, connection, bytes) =>
                {
                    try
                    {
                        var envelope = Any.Parser.ParseFrom(bytes);
                        if (envelope.TypeUrl == "Phinix/UserManagement.LoginPacket")
                        {
                            var login = envelope.Unpack<LoginPacket>();
                            if (login.DisplayName != "synthetic-historical-rework" || string.IsNullOrEmpty(login.SessionId))
                                throw new InvalidOperationException();
                            login.SessionId = "";
                            byte[] derived = ProtobufPacketHelper.Pack(login).ToByteArray();
                            lock (gate) fixtures.Add(new {
                                direction = "historical-client-to-current-component-peer", module, typeUrl = envelope.TypeUrl,
                                classification = "redacted-derived-from-fixed-historical-client-process",
                                rawSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                                derivedSha256 = Convert.ToHexString(SHA256.HashData(derived)).ToLowerInvariant(),
                                derivedAnyBase64 = Convert.ToBase64String(derived),
                                transformation = "Remove SessionId; preserve every other synthetic historical login field"
                            });
                        }
                    }
                    catch { System.Threading.Interlocked.Increment(ref errors); }
                    original(module, connection, bytes);
                };
                net.Start();
                int port = ((NetManager)typeof(NetServer).GetField("server", Private).GetValue(net)).LocalPort;
                if (!net.Listening || port == 0) throw new InvalidOperationException();
                Console.WriteLine(JsonSerializer.Serialize(new { ready = true, port }));
                Console.Out.Flush();
                if (Console.ReadLine() != "stop") return 1;
                net.Stop();
                lock (gate)
                {
                    if (errors != 0 || fixtures.Count != 2) return 1;
                    File.WriteAllText(args[0], JsonSerializer.Serialize(new {
                        schema = 1, profile = "HistoricalRework", fixtures,
                        clientSha = "23643e194c254026b3665ced1e618123d68f7566",
                        commonSha = "9d0710aaa9b41c07f28d9bfa25599247e7bb1f93",
                        protobufSha = "4b0c3aacf0657fbf38253b38918d3358dd4319ec",
                        limitations = "Login/auth module subset only; no Framework host, negotiation, Trade, published Server or game delivery"
                    }));
                }
                return 0;
            }
            finally { auth.Stop(); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("HISTORICAL PEER failed: " + error.GetType().Name);
            return 1;
        }
    }
}
