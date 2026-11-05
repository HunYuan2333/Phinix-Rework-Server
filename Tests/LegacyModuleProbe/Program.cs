using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using Authentication;
using Chat;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LiteNetLib;
using UserManagement;
using Utils;

// Independent original-module fixture producer. No modern Server/Adapter references.
// Requires Mono for the historical Thread.Abort lifecycle; not a replacement server harness.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> fixtures = new List<object>();
    private static readonly object fixtureLock = new object();
    private static int captureErrors;

    private static string Hash(byte[] bytes)
    {
        using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(bytes).Select(b => b.ToString("x2")));
    }

    private static void Tap(NetCommon net, string direction)
    {
        var handlers = (Dictionary<string, NetCommon.PacketHandlerDelegate>)typeof(NetCommon).GetField("registeredPacketHandlers", Private).GetValue(net);
        foreach (string key in handlers.Keys.ToArray())
        {
            var original = handlers[key];
            handlers[key] = (module, connection, bytes) =>
            {
                try { Capture(direction, module, bytes); }
                catch { Interlocked.Increment(ref captureErrors); }
                original(module, connection, bytes);
            };
        }
    }

    private static void Capture(string direction, string module, byte[] bytes)
    {
        // Authentication and public user packets can contain credentials in the old source.
        // They are never retained or emitted by this producer.
        if (module != "Chat" && module != "UserManagement") return;
        Any envelope = Any.Parser.ParseFrom(bytes);
        IMessage sanitized = null;
        string transformation = null;
        if (envelope.TypeUrl.EndsWith("/UserManagement.LoginPacket", StringComparison.Ordinal))
        {
            var login = envelope.Unpack<LoginPacket>();
            if (login.DisplayName != "synthetic-probe-user") throw new InvalidOperationException();
            login.SessionId = "";
            sanitized = login;
            transformation = "Remove SessionId; preserve all other synthetic login fields";
        }
        else if (envelope.TypeUrl.EndsWith("/Chat.ChatMessagePacket", StringComparison.Ordinal))
        {
            var chat = envelope.Unpack<ChatMessagePacket>();
            if (chat.Message != "synthetic-probe-message") return;
            chat.SessionId = ""; chat.Uuid = "SENDER"; chat.MessageId = "MESSAGE"; chat.Timestamp = null;
            sanitized = chat;
            transformation = "Remove SessionId/Timestamp; normalize synthetic Uuid and MessageId";
        }
        else if (envelope.TypeUrl.EndsWith("/Chat.ChatMessageResponsePacket", StringComparison.Ordinal))
        {
            var response = envelope.Unpack<ChatMessageResponsePacket>();
            if (!response.Success || response.Message != "synthetic-probe-message") throw new InvalidOperationException();
            response.OriginalMessageId = "MESSAGE"; response.NewMessageId = "CONFIRMED_MESSAGE";
            sanitized = response;
            transformation = "Normalize original/new synthetic message IDs; preserve actual Success and Message";
        }
        if (sanitized == null) return;
        byte[] derived = ProtobufPacketHelper.Pack(sanitized).ToByteArray();
        lock (fixtureLock) fixtures.Add(new
        {
            direction, module, typeUrl = envelope.TypeUrl, classification = "redacted-derived-from-original-module-loopback-capture",
            rawSha256 = Hash(bytes), derivedSha256 = Hash(derived), derivedAnyBase64 = Convert.ToBase64String(derived), transformation
        });
    }

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--trade") return TradeProbe.Run(Path.GetFullPath(args[1]));
        if (args.Length != 1) return 2;
        string output = Path.GetFullPath(args[0]);
        string privateDirectory = Path.Combine(Path.GetTempPath(), "phinix-old-identity-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(privateDirectory);
        NetServer server = null;
        NetClient client = null;
        ServerAuthenticator serverAuth = null;
        ClientAuthenticator clientAuth = null;
        try
        {
            server = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 2);
            serverAuth = new ServerAuthenticator(server, "Synthetic original", "Isolated module probe", AuthTypes.ClientKey);
            var serverUsers = new ServerUserManager(server, serverAuth);
            var serverChat = new ServerChat(server, serverAuth, serverUsers, 4);
            client = new NetClient();
            clientAuth = new ClientAuthenticator(client, (session, name, description, type, callback) => callback(false, session, type, "", ""),
                                                 Path.Combine(privateDirectory, "credentials.bin"));
            var clientUsers = new ClientUserManager(client, clientAuth);
            var clientChat = new ClientChat(client, clientAuth, clientUsers);
            using (var authenticated = new ManualResetEventSlim())
            using (var loggedIn = new ManualResetEventSlim())
            using (var confirmed = new ManualResetEventSlim())
            {
                clientAuth.OnAuthenticationSuccess += (_, __) => authenticated.Set();
                clientUsers.OnLoginSuccess += (_, __) => loggedIn.Set();
                clientChat.OnChatMessageReceived += (_, e) =>
                {
                    if (e.Message.Message == "synthetic-probe-message" && e.Message.Status == ChatMessageStatus.CONFIRMED) confirmed.Set();
                };
                Tap(server, "client-to-original-server");
                Tap(client, "original-server-to-client");
                server.Start();
                var manager = (NetManager)typeof(NetServer).GetField("server", Private).GetValue(server);
                if (manager.LocalPort == 0) throw new InvalidOperationException("Original server did not bind an ephemeral port");
                client.Connect(new IPEndPoint(IPAddress.Loopback, manager.LocalPort));
                if (!authenticated.Wait(5000)) throw new InvalidOperationException("Original automatic authentication deadline");
                clientUsers.SendLogin("synthetic-probe-user");
                if (!loggedIn.Wait(5000)) throw new InvalidOperationException("Original login deadline");
                clientChat.Send("synthetic-probe-message");
                if (!confirmed.Wait(5000)) throw new InvalidOperationException("Original chat confirmation deadline");
                if (captureErrors != 0) throw new InvalidOperationException("Fixture capture failed");
            }
            lock (fixtureLock)
            {
                if (fixtures.Count != 3) throw new InvalidOperationException("Expected login/request/actual response captures");
                var json = new JavaScriptSerializer().Serialize(new
                {
                    schema = 1, sourceSha = "ce82b8f2cd57718f05de86458eebd3320f35c4d8",
                    protobufSha = "dd06cc6c8f906b760ef224a085f45e7694dc426a", profile = "Original",
                    fixtures, assertions = new[] { "automatic authentication", "login", "ClientChat.Send over real UDP", "actual correlated chat Success" },
                    limitations = "Original modules only; not upgraded Server support, raw corpus, asset delivery, or reliable completion ACK"
                });
                File.WriteAllText(output, json);
            }
            Console.WriteLine("LEGACY MODULE RESULT passed; captures=3; realUDP=true; reliableAssetAck=false");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("LEGACY MODULE RESULT failed: " + error.GetType().Name);
            return 1;
        }
        finally
        {
            client?.Disconnect();
            server?.Stop();
            if (serverAuth != null) ((System.Timers.Timer)typeof(ServerAuthenticator).GetField("sessionCleanupTimer", Private).GetValue(serverAuth)).Dispose();
            if (clientAuth != null) ((System.Timers.Timer)typeof(ClientAuthenticator).GetField("sessionExtendTimer", Private).GetValue(clientAuth)).Dispose();
            System.IO.Directory.Delete(privateDirectory, true);
        }
    }
}
