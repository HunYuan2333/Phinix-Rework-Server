using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Text.Json;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Phinix.ChatExtension.Server;
using Phinix.LegacyAdapter.Server;
using PhinixServer;
using ServerRuntime;
using UserManagement;
using Utils;
using Utils.Framework;
using Message = Phinix.Framework.BuiltInChatMessagePayload;

internal static partial class Program
{
    private static int checks;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
    private static void Refused(Action action)
    { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Expected explicit representability refusal"); }
    private static byte[] Request(string id, string text = "synthetic-message", string principal = "a", string session = "synthetic-session")
    {
        // Independent fixed .proto field writer, never calls the adapter to generate requests.
        using var stream = new MemoryStream();
        using (var w = new CodedOutputStream(stream, true))
        {
            foreach (var item in new[] { (1, session), (2, principal), (3, text), (5, id) })
            { w.WriteTag(item.Item1, WireFormat.WireType.LengthDelimited); w.WriteString(item.Item2); }
            w.Flush();
        }
        return new Any { TypeUrl = "Phinix/Chat.ChatMessagePacket", Value = ByteString.CopyFrom(stream.ToArray()) }.ToByteArray();
    }
    private static Dictionary<int, string> Response(byte[] bytes)
    {
        var any = Any.Parser.ParseFrom(bytes); Check(any.TypeUrl == "Phinix/Chat.ChatMessageResponsePacket", "Wrong response wire type");
        var result = new Dictionary<int, string>(); using var r = new CodedInputStream(any.Value.ToByteArray()); uint tag;
        while ((tag = r.ReadTag()) != 0)
        {
            int field = (int)(tag >> 3);
            result.Add(field, field == 1 ? r.ReadBool().ToString() : r.ReadString());
        }
        return result;
    }
    private static Message Plain(string text = "text") => new() { SenderUuid = "a", MessageId = "stored", Message = text, Timestamp = Timestamp.FromDateTime(DateTime.UtcNow) };
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0].StartsWith("--audit-chat-", StringComparison.Ordinal))
        { AuditChat(args[0]); return; }
        if (args.Length == 2 && (args[0] == "--write-config" || args[0] == "--write-trade-config"))
        {
            var config = new Config { Address = IPAddress.Any, Port = 0, SaveInterval = 60000, LogPath = "server.log", UserDatabasePath = "users", CredentialDatabasePath = "credentials" };
            // The chat-only probe exercises explicit trade refusal. The mixed-trade probe uses defaults.
            if (args[0] == "--write-config")
            {
                using var tradeXml = new MemoryStream(); new DataContractSerializer(typeof(OriginalTradeServerConfig)).WriteObject(tradeXml, new OriginalTradeServerConfig { Enabled = false });
                config.ExtensionConfigs["builtin.original-trade-adapter"] = System.Text.Encoding.UTF8.GetString(tradeXml.ToArray());
            }
            config.Save(args[1]); return;
        }
        Check(new Config().GetConfig<LegacyChatServerConfig>().Enabled, "Original chat must be enabled with no saved section");
        var decoded = OriginalChatCodec.DecodeRequest(Request("id"));
        Check(decoded.OriginalId == "id" && decoded.Text == "synthetic-message" && decoded.PrincipalId == "a", "Independent original fields misread");
        Refused(() => OriginalChatCodec.DecodeRequest(new byte[32 * 1024 + 1]));
        var bad = Any.Parser.ParseFrom(Request("bad")); bad.TypeUrl += "suffix"; Refused(() => OriginalChatCodec.DecodeRequest(bad.ToByteArray()));
        bad = Any.Parser.ParseFrom(Request("bad")); bad.Value = ByteString.CopyFrom(bad.Value.Concat(new byte[] { 50, 1, 65 }).ToArray());
        Refused(() => OriginalChatCodec.DecodeRequest(bad.ToByteArray()));
        var reply = Plain(); reply.ReplyToMessageId = "previous"; Refused(() => OriginalChatCodec.Broadcast(reply));
        var mention = Plain(); mention.MentionedUuids.Add("b"); Refused(() => OriginalChatCodec.History(new[] { Plain(), mention }));
        var notice = Plain(); notice.IsNotice = true; Refused(() => OriginalChatCodec.Broadcast(notice));
        Refused(() => OriginalChatCodec.History(new[] { Plain(new string('x', OriginalChatCodec.MaximumPacketBytes)) }));
        var net = new Wire(); var directory = new Directory(); var ready = new Readiness();
        var chat = new PhinixFrameworkChatService(40, 2, 24, directory); var publications = new List<Message>(); var warnings = new List<string>();
        using (var adapter = new OriginalChatAdapter(net, directory, directory, ready, chat, publications.Add, (m, _) => warnings.Add(m)))
        {
            adapter.Start(); directory.Login("a"); directory.Login("b");
            Check(net.Sent.Count(p => Any.Parser.ParseFrom(p.Bytes).TypeUrl.EndsWith("ChatHistoryPacket")) == 2, "Login history not sent to original peers");
            net.Sent.Clear(); net.Receive("a", Request("one"));
            var response = Response(net.Sent.Single(p => p.Peer == "a").Bytes);
            Check(response[1] == "True" && response[2] == "one" && response[3] != "one", "Success is not correlated to service assigned ID");
            Check(chat.GetHistory().Length == 1 && chat.GetHistory()[0].MessageId == response[3] && publications.Count == 1, "Legacy path does not share real service/history/publication");
            Check(Any.Parser.ParseFrom(net.Sent.Single(p => p.Peer == "b").Bytes).TypeUrl.EndsWith("ChatMessagePacket"), "Other original participant missing real broadcast");
            net.Sent.Clear(); net.Receive("a", Request("one"));
            Check(chat.GetHistory().Length == 1 && publications.Count == 1 && Response(net.Sent.Single().Bytes)[3] == response[3], "Identical application replay created new message/result");
            net.Sent.Clear(); net.Receive("a", Request("one", "conflicting-text"));
            Check(!Response(net.Sent.Single().Bytes).ContainsKey(1) && chat.GetHistory().Length == 1, "Conflicting request falsely accepted");
            net.Sent.Clear(); net.Receive("a", Request("forged", principal: "b"));
            Check(!Response(net.Sent.Single().Bytes).ContainsKey(1) && chat.GetHistory().Length == 1, "Claimed other principal bypasses authority");
            net.Sent.Clear(); net.Receive("a", Request("expired", session: "bad"));
            Check(!Response(net.Sent.Single().Bytes).ContainsKey(1) && chat.GetHistory().Length == 1, "Bad session accepted");
            ready.Selected = "a"; net.Sent.Clear(); net.Receive("a", Request("switch"));
            Check(!Response(net.Sent.Single().Bytes).ContainsKey(1) && chat.GetHistory().Length == 1, "Ready Framework connection switched raw profile"); ready.Selected = null;
            net.Outcome = TransportSendOutcome.Unknown; net.Receive("a", Request("lost-response"));
            Check(chat.GetHistory().Length == 2, "Local send result replaced the actual accepted message");
            net.Outcome = TransportSendOutcome.Submitted; net.Sent.Clear(); net.Receive("a", Request("lost-response"));
            Check(Response(net.Sent.Single().Bytes)[1] == "True" && chat.GetHistory().Length == 2, "Response loss retried business mutation instead of original result");
            // Real modern-style service input: direct same API, not an invented old response.
            net.Sent.Clear(); chat.AddMessage("modern", "modern-plain");
            Check(net.Sent.Count == 2, "Modern plain service message not translated to original peers");
            net.Sent.Clear(); chat.AddMessage("modern", "structured", new[] { "a" }, null, null);
            Check(net.Sent.Count == 2 && net.Disconnected.Count == 0 && net.Sent.All(p => ChatText(p.Bytes) == "[Mentions: a (current name)]\nstructured"), "Mention context not explicitly projected, or it disconnected original peers");
            directory.Login("c"); net.Sent.Clear(); int count = chat.GetHistory().Length; net.Receive("c", Request("history-blocked", principal: "c"));
            Check(Response(net.Sent.Single(p => p.Peer == "c").Bytes)[1] == "True" && chat.GetHistory().Length == count + 1, "Structured history blocked an independent plain-message result");
        }
        Check(!net.Handlers.ContainsKey("Chat"), "Adapter shutdown leaked raw handler");
        int sent = net.Sent.Count; directory.Login("after-stop"); chat.AddMessage("modern", "after-stop");
        Check(net.Sent.Count == sent, "Disposed adapter retained event subscription");
        // Notification isolation: one consumer cannot corrupt the next consumer or the stored result.
        Message observed = null; chat.MessageStored += m => { m.Message = "mutated"; throw new Exception("synthetic"); };
        chat.MessageStored += m => observed = m; var added = chat.AddMessage("modern", "isolated");
        Check(added.Message == "isolated" && observed.Message == "isolated" && chat.GetHistory().Last().Message == "isolated", "Neutral subscriber changed authority result or blocked next subscriber");
        // Stable barriers reproduce NetCommon's handler-lock -> adapter-gate order.
        // A shutdown must release its gate before waiting to unregister the module.
        var concurrentWire = new Wire(); var concurrentUsers = new Directory();
        using (var concurrent = new OriginalChatAdapter(concurrentWire, concurrentUsers, concurrentUsers, new Readiness(), chat, _ => { }))
        using (var held = new System.Threading.ManualResetEventSlim())
        using (var release = new System.Threading.ManualResetEventSlim())
        using (var unregister = new System.Threading.ManualResetEventSlim())
        {
            concurrent.Start(); concurrentUsers.Login("a");
            concurrentWire.BeforeReceive = () => { held.Set(); release.Wait(); };
            concurrentWire.BeforeUnregister = () => unregister.Set();
            var receiving = System.Threading.Tasks.Task.Run(() => concurrentWire.Receive("a", Request("shutdown-barrier")));
            Check(held.Wait(2000), "Packet did not reach held dispatch barrier");
            var stopping = System.Threading.Tasks.Task.Run(() => concurrent.Dispose());
            try { Check(unregister.Wait(2000), "Shutdown did not reach unregister barrier"); }
            finally { release.Set(); }
            Check(System.Threading.Tasks.Task.WaitAll(new[] { receiving, stopping }, 2000), "Shutdown reversed packet handler lock order");
        }
        ChatProjectionRegressions();
        ChatNameSnapshotRegressions();
        foreach (var scenario in new[] { "logging", "size", "dependency", "redaction", "no-recipient" }) AuditChat("--audit-chat-" + scenario);
        Console.WriteLine($"ORIGINAL CHAT RESULT passed; checks={checks}; realService=true; authorityPortIsolated=true; nativeNetwork=false");
    }
    private static void AuditChat(string scenario)
    {
        if (scenario == "--audit-chat-dependency")
        {
            var types = new[] { typeof(LegacyChatServerExtension), typeof(BuiltInChatServerExtension), typeof(OriginalTradeRefusalAdapter), typeof(Phinix.TradeExtension.Server.BuiltInTradeServerExtension) };
            var graph = ExtensionDependencyGraph.Build(types);
            Check(graph.GetDependencies("builtin.legacy-chat-adapter").SequenceEqual(new[] { "builtin.chat" }), "Chat adapter has no explicit provider dependency");
            var order = graph.GetStableTopologicalOrder(types).ToList();
            Check(order.IndexOf(typeof(BuiltInChatServerExtension)) < order.IndexOf(typeof(LegacyChatServerExtension)), "Adapter can activate before provider");
            Check(graph.GetDisabledDependencies("builtin.legacy-chat-adapter", new[] { "builtin.chat" }).Count == 1 && graph.GetDependencies("builtin.original-trade-refusal").SequenceEqual(new[] { "builtin.trade" }),
                "Provider disable does not isolate only its dependent adapter");
            graph = ExtensionDependencyGraph.Build(new[] { typeof(LegacyChatServerExtension), typeof(OriginalTradeRefusalAdapter) });
            Check(graph.TryGetValidationError("builtin.legacy-chat-adapter", out _) && graph.TryGetValidationError("builtin.original-trade-refusal", out _), "Missing providers must fail their dependent adapters");
        }
        else if (scenario == "--audit-chat-redaction")
        {
            var request = new OriginalChatCodec.Request("synthetic-session-secret", "a", "id", "synthetic-private-text");
            Check(!request.ToString().Contains("synthetic-session-secret") && !request.ToString().Contains("synthetic-private-text"), "Chat request diagnostic leaks session or private text");
        }
        else
        {
            var net = new Wire(); var users = new Directory(); var warnings = new List<string>();
            var chat = new PhinixFrameworkChatService(40, 2, 24, users);
            using var adapter = new OriginalChatAdapter(net, users, users, new Readiness(), chat, _ => { }, (m, _) => warnings.Add(m));
            adapter.Start();
            if (scenario == "--audit-chat-no-recipient")
            {
                chat.AddMessage("modern", "structured-without-original-recipient", new[] { "b" }, null, null);
                Check(warnings.Count == 0 && net.Sent.Count == 0, "Adapter reported an expressibility failure with no original recipient");
                Console.WriteLine($"P5 CHAT AUDIT RESULT passed; scenario={scenario}; checks={checks}");
                return;
            }
            users.Login("a"); users.Login("b"); net.Sent.Clear();
            if (scenario == "--audit-chat-logging")
            {
                int laterLogger = 0;
                chat.OnLogEntry += (_, _) => throw new InvalidOperationException("synthetic-private-logger");
                chat.OnLogEntry += (_, _) => laterLogger++;
                net.Receive("a", Request("logging"));
                var first = Response(net.Sent.Single(p => p.Peer == "a").Bytes); net.Sent.Clear();
                net.Receive("a", Request("logging"));
                Check(chat.GetHistory().Length == 1 && laterLogger == 1 && Response(net.Sent.Single().Bytes)[3] == first[3], "Diagnostic failure duplicated accepted message or lost original result");
            }
            else if (scenario == "--audit-chat-size")
            {
                net.Receive("a", Request("select-a")); net.Receive("b", Request("select-b", principal: "b")); net.Sent.Clear();
                chat.AddMessage("modern", new string('x', OriginalChatCodec.MaximumPacketBytes + 512));
                Check(net.Disconnected.SetEquals(new[] { "a", "b" }) && net.Sent.Count == 0 && warnings.Contains("OriginalChatMessageUnrepresentable"), "Oversize message silently skipped instead of refusing every affected original route");
            }
            else throw new InvalidOperationException("Unknown audit scenario");
        }
        Console.WriteLine($"P5 CHAT AUDIT RESULT passed; scenario={scenario}; checks={checks}");
    }
    private sealed class Readiness : IServerSessionReadiness
    { public string Selected; public event EventHandler<ServerSessionReadyEventArgs> SessionReady { add { } remove { } } public bool TryGetReady(string id, out ServerSessionReadyEventArgs s) { s = null; return id == Selected; } }
    // Unit boundary isolates only external transport/identity ports. Actual auth is checked by separate native network peer.
    private sealed class Directory : IServerUserManager, IServerPrincipalAuthorization
    {
        private readonly HashSet<string> users = new(); public event EventHandler<ServerLoginEventArgs> OnLogin;
        public void Login(string id) { users.Add(id); OnLogin?.Invoke(this, new ServerLoginEventArgs(id, id)); }
        public bool IsAuthenticatedPrincipal(string c, string s, string p) => users.Contains(c) && c == p && s == "synthetic-session";
        public bool IsLoggedIn(string c, string p) => users.Contains(c) && c == p;
        public string[] GetConnections() => users.ToArray();
        public bool TryGetConnection(string id, out string c) { c = id; return users.Contains(id); }
        public readonly Dictionary<string, string> Names = new();
        public bool TryGetDisplayName(string id, out string name) { name = Names.TryGetValue(id, out var actual) ? actual : id; return true; }
        public bool TryGetLoggedIn(string id, out bool loggedIn) { loggedIn = users.Contains(id); return true; }
        public bool TryGetAcceptingTrades(string id, out bool accepting) { accepting = true; return true; }
    }
    private sealed class Wire : IServerTransport
    {
        public readonly Dictionary<string, NetCommon.PacketHandlerDelegate> Handlers = new();
        public readonly List<(string Peer, byte[] Bytes)> Sent = new(); public readonly HashSet<string> Disconnected = new();
        public TransportSendOutcome Outcome = TransportSendOutcome.Submitted;
        public Action BeforeReceive, BeforeUnregister;
        private readonly object handlerGate = new();
        public void Receive(string peer, byte[] bytes) { lock (handlerGate) { BeforeReceive?.Invoke(); Handlers["Chat"]("Chat", peer, bytes); } }
        public IPEndPoint Endpoint => new(IPAddress.Loopback, 0); public int LocalPort => 0; public bool Listening => true;
        public event EventHandler<LogEventArgs> OnLogEntry { add { } remove { } } public void RaiseLogEntry(LogEventArgs e) { }
        public event EventHandler PollCompleted { add { } remove { } }
        public event EventHandler<ConnectionEventArgs> OnConnectionEstablished { add { } remove { } }
        public event EventHandler<ConnectionEventArgs> OnConnectionClosed { add { } remove { } }
        public void RegisterPacketHandler(string module, NetCommon.PacketHandlerDelegate handler) { lock (handlerGate) Handlers.Add(module, handler); }
        public void UnregisterPacketHandler(string module) { BeforeUnregister?.Invoke(); lock (handlerGate) Handlers.Remove(module); } public void UnregisterAllPacketHandlers() => Handlers.Clear();
        public void Start() { } public void Stop() { } public void Dispose() { }
        public void Send(string c, string m, byte[] p) => TrySendWithOutcome(c, m, p);
        public bool TrySend(string c, string m, byte[] p) => TrySendWithOutcome(c, m, p) == TransportSendOutcome.Submitted;
        public TransportSendOutcome TrySendWithOutcome(string c, string m, byte[] p) { Sent.Add((c, p)); return Outcome; }
        public void DisconnectPeer(string id) => Disconnected.Add(id);
    }
}
