using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using Authentication;
using Chat;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using UserManagement;
using Utils;

// Compiled ONLY against the pinned, independently built thomotron original modules.
// No reference to upgraded Server, its adapter, its Common or its serializers.
internal static partial class Program
{
    private static int checks;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
    private sealed partial class Peer : IDisposable
    {
        public readonly NetClient Net = new NetClient();
        public readonly ClientAuthenticator Auth;
        public readonly ClientUserManager Users;
        public readonly ClientChat Chat;
        public readonly ManualResetEventSlim History = new ManualResetEventSlim();
        private readonly ManualResetEventSlim authenticated = new ManualResetEventSlim(), loggedIn = new ManualResetEventSlim(), changed = new ManualResetEventSlim();
        private readonly object gate = new object();
        private readonly List<ChatMessageResponsePacket> responses = new List<ChatMessageResponsePacket>();
        public Peer(int port, string path, string name)
        {
            Directory.CreateDirectory(path);
            Auth = new ClientAuthenticator(Net, (s, n, d, t, cb) => cb(false, s, t, "", ""), Path.Combine(path, "credential.bin"));
            Users = new ClientUserManager(Net, Auth); Chat = new ClientChat(Net, Auth, Users);
            InitializeTrade();
            Auth.OnAuthenticationSuccess += (_, __) => authenticated.Set(); Users.OnLoginSuccess += (_, __) => loggedIn.Set();
            Chat.OnChatSync += (_, __) => { History.Set(); changed.Set(); }; Chat.OnChatMessageReceived += (_, __) => changed.Set();
            var handlers = (Dictionary<string, NetCommon.PacketHandlerDelegate>)typeof(NetCommon).GetField("registeredPacketHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Net);
            var original = handlers["Chat"];
            handlers["Chat"] = (module, connection, bytes) =>
            {
                var any = Any.Parser.ParseFrom(bytes);
                if (any.TypeUrl == "Phinix/Chat.ChatMessageResponsePacket")
                    lock (gate) responses.Add(any.Unpack<ChatMessageResponsePacket>());
                original(module, connection, bytes); changed.Set();
            };
            try
            {
                Net.Connect(new IPEndPoint(IPAddress.Loopback, port));
                Check(authenticated.Wait(8000), "native automatic authentication timeout");
                Users.SendLogin(name); Check(loggedIn.Wait(8000), "native login timeout");
                Check(History.Wait(8000), "native initial history timeout");
            }
            catch { Dispose(); throw; }
        }
        public void Await(Func<bool> condition, string reason, int seconds = 8)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition()) { if (DateTime.UtcNow >= deadline) throw new Exception(reason); changed.Wait(50); changed.Reset(); }
            checks++;
        }
        public ChatMessageResponsePacket AwaitResponse(string id, int prior = 0)
        {
            Await(() => { lock (gate) return responses.Count(r => r.OriginalMessageId == id) > prior; }, "native correlated response timeout");
            lock (gate) return responses.Last(r => r.OriginalMessageId == id);
        }
        public ChatMessageResponsePacket Send(string text)
        {
            Chat.Send(text); Await(() => Chat.GetMessages(false).Any(m => m.Message == text && m.Status == ChatMessageStatus.CONFIRMED), "actual native ClientChat failed to confirm");
            lock (gate) return responses.Last(r => r.Message == text && r.Success);
        }
        public void Raw(string id, string text, string uuid = null, string session = null)
        {
            var request = new ChatMessagePacket { SessionId = session ?? Auth.SessionId, Uuid = uuid ?? Users.Uuid, MessageId = id, Message = text };
            Net.Send("Chat", ProtobufPacketHelper.Pack(request).ToByteArray());
        }
        public void Dispose()
        {
            Net.Disconnect();
            ((System.Timers.Timer)typeof(ClientAuthenticator).GetField("sessionExtendTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Auth)).Dispose();
        }
    }
    private static int Main(string[] args)
    {
        try
        {
            int port = int.Parse(args[0]); string root = Path.GetFullPath(args[1]); string phase = args[2];
            using (var a = new Peer(port, Path.Combine(root, "a"), "synthetic-original-a"))
            using (var b = new Peer(port, Path.Combine(root, "b"), "synthetic-original-b"))
            {
                if (phase.StartsWith("trade-", StringComparison.Ordinal)) TradeChecks(a, b, root, phase);
                else if (phase == "mixed")
                {
                    a.Await(() => File.Exists(Path.Combine(root, "modern-ready")), "Framework Ready barrier");
                    a.Send("mix-original-start");
                    File.WriteAllText(Path.Combine(root, "original-ready"), a.Users.Uuid);
                    a.Await(() => a.Chat.GetMessages(false).Any(m => m.Message == "mix-modern"), "Framework plain message absent from actual original ClientChat");
                    a.Send("mix-original-return");
                    a.Await(() => File.Exists(Path.Combine(root, "modern-verified")), "Modern mixed verification barrier");
                    a.Net.Send("Trading", new byte[] { 1, 2, 3 });
                    a.Await(() => !a.Net.Connected, "Unsafe original Trading route not explicitly disconnected");
                    Check(b.Net.Connected, "Original trade refusal disconnected unrelated native peer");
                    File.WriteAllText(Path.Combine(root, "original-second"), b.Users.Uuid);
                    b.Send("mix-original-structured-ready");
                    var parent = b.Chat.GetMessages(false).Single(m => m.Message == "mix-modern");
                    Check(b.Users.TryGetDisplayName(parent.SenderUuid, out var parentName), "Reply author name missing from original directory");
                    Check(b.Users.TryGetDisplayName(b.Users.Uuid, out var mentionedName), "Mentioned name missing from original directory");
                    b.Await(() => b.Chat.GetMessages(false).Any(m => m.Message == "[Reply " + parentName + "] mix-modern\n[Mentions: " + mentionedName + " (current name)]\nmix-structured"), "Original reply/mention text projection missing");
                    Check(b.Net.Connected, "Read-only structured projection disconnected original client");
                    b.Send("mix-after-structured");
                    File.WriteAllText(Path.Combine(root, "original-structured-verified"), "verified");
                }
                else if (phase == "structured-restart")
                {
                    foreach (var peer in new[] { a, b })
                        Check(peer.Chat.GetMessages(false).Any(m => m.Message.Contains("[Reply ") && m.Message.Contains("[Mentions: ") && m.Message.EndsWith("mix-structured", StringComparison.Ordinal)), "Structured history did not bootstrap through actual original decoder");
                    a.Send("native-after-structured-history");
                    b.Await(() => b.Chat.GetMessages(false).Any(m => m.Message == "native-after-structured-history"), "Reply-containing history blocked live broadcast");
                }
                else if (phase == "initial")
                {
                    Check(a.Chat.GetMessages(false).Length == 0 && b.Chat.GetMessages(false).Length == 0, "fresh native histories not empty");
                    var first = a.Send("native-a"); Check(first.NewMessageId != first.OriginalMessageId, "server did not assign authoritative ID");
                    b.Await(() => b.Chat.GetMessages(false).Any(m => m.MessageId == first.NewMessageId && m.Message == "native-a" && m.SenderUuid == a.Users.Uuid), "native peer broadcast missing");
                    var second = b.Send("native-b"); a.Await(() => a.Chat.GetMessages(false).Any(m => m.MessageId == second.NewMessageId), "reverse native broadcast missing");
                    a.Raw(first.OriginalMessageId, "native-a"); var replay = a.AwaitResponse(first.OriginalMessageId, 1);
                    Check(replay.Success && replay.NewMessageId == first.NewMessageId, "application replay lost original result");
                    a.Raw(first.OriginalMessageId, "conflicting"); Check(!a.AwaitResponse(first.OriginalMessageId, 2).Success, "conflict accepted");
                    a.Raw("forged-owner", "must-not-store", b.Users.Uuid); Check(!a.AwaitResponse("forged-owner").Success, "cross-owner request accepted");
                    a.Raw("bad-session", "must-not-store", session: "invalid-synthetic-session"); Check(!a.AwaitResponse("bad-session").Success, "bad session accepted");
                    Check(a.Chat.GetMessages(false).Length == 2 && b.Chat.GetMessages(false).Length == 2, "replay or refusal changed native histories");
                    File.WriteAllText(Path.Combine(root, "expected.json"), new JavaScriptSerializer().Serialize(new { a = a.Users.Uuid, b = b.Users.Uuid, first = first.NewMessageId, second = second.NewMessageId }));
                }
                else
                {
                    var expected = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "expected.json")));
                    Check(a.Users.Uuid == expected["a"] && b.Users.Uuid == expected["b"], "original UUID changed after published Server restart");
                    foreach (var peer in new[] { a, b })
                    {
                        var messages = peer.Chat.GetMessages(false);
                        Check(messages.Length == 2 && messages.Any(m => m.MessageId == expected["first"] && m.Message == "native-a") && messages.Any(m => m.MessageId == expected["second"] && m.Message == "native-b"), "native decoder did not restore exact shared authoritative history");
                    }
                    a.Send("native-after-restart"); b.Await(() => b.Chat.GetMessages(false).Any(m => m.Message == "native-after-restart"), "native post-restart message missing");
                }
            }
            Console.WriteLine("ORIGINAL CLIENT RESULT passed; phase=" + phase + "; checks=" + checks + "; realOriginalModules=true; realUDP=true; gameAcceptance=false");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("ORIGINAL CLIENT RESULT failed: " + error.GetType().Name + ": " + error.Message); return 1; }
    }
}
