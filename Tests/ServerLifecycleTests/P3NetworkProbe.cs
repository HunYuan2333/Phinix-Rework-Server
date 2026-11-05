using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Threading;
using Authentication;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LiteNetLib;
using LiteNetLib.Utils;
using Phinix.TradeExtension;
using UserManagement;
using Utils;
using Utils.Framework;

// Peer framing derived from the frozen 2026-10-18 independent wire probe;
// original SHA256 baaf5c6180140a8fa27c978bf30f23e496b445b43ad8c74d2abd71a2240f7b5c.
// No server adapter/service types are used to construct expected results.
internal static partial class P3NetworkProbe
{
    private sealed class State
    {
        public string[] Keys { get; set; }
        public string[] Principals { get; set; }
        public string Trade { get; set; }
        public string NewTrade { get; set; }
    }
    [DataContract] private sealed class Query
    {
        [DataMember(Order=0)] public string OperationId { get; set; }
        [DataMember(Order=1)] public string RequestFingerprint { get; set; }
    }
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    private static void Wait(Peer a, Peer b, Func<bool> done)
    { var t=Stopwatch.StartNew(); while(!done() && t.ElapsedMilliseconds < 10000) { a.Pump(); b.Pump(); Thread.Sleep(5); } Check(done(), "P3 wire deadline"); }
    private static FrameworkTradeCreateResponse Create(Peer a, Peer b, string id)
    {
        a.Command(FrameworkTradeProtocol.CreateRequestType, new FrameworkTradeCreateRequest { OtherPartyUuid=b.Principal }, id);
        Wait(a,b,()=>a.Find(FrameworkTradeProtocol.CreateResponseType,id)!=null);
        return FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(a.Find(FrameworkTradeProtocol.CreateResponseType,id).PayloadJson);
    }
    private static JsonElement Lookup(Peer a, Peer b, string operation, string fingerprint, string queryId)
    {
        a.Command("trade.operation.query.request", new Query { OperationId=operation, RequestFingerprint=fingerprint }, queryId);
        Wait(a,b,()=>a.Find("trade.operation.query.response",queryId)!=null);
        return JsonDocument.Parse(a.Find("trade.operation.query.response",queryId).PayloadJson).RootElement.Clone();
    }
    private static string Fingerprint<T>(string kind,T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind+"\n"+FrameworkSerialization.SerializePayload(value))));
    public static void Run(int port,string phase,string path)
    {
        bool initial=phase=="initial";
        var state=initial ? new State { Keys=new[]{Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")} } : JsonSerializer.Deserialize<State>(File.ReadAllText(path));
        using var a=new Peer(port,state.Keys[0]); using var b=new Peer(port,state.Keys[1]);
        Wait(a,b,()=>a.Ready && b.Ready);
        if(phase=="bad-config")
        {
            var capabilities=FrameworkSerialization.DeserializePayload<FrameworkCapabilitiesPayload>(a.Packets.First(p=>p.Kind==FrameworkProtocol.KindCapabilities).PayloadJson);
            Check(!capabilities.Capabilities.Contains(FrameworkTradeProtocol.Capability),"Corrupt trade config silently activated default limits");
            a.Command(FrameworkTradeProtocol.CreateRequestType,new FrameworkTradeCreateRequest{OtherPartyUuid=b.Principal},"bad-config-create");
            Wait(a,b,()=>a.Packets.Any(p=>p.MessageType=="framework.admission.rejected"));
            var control=JsonDocument.Parse(a.Packets.First(p=>p.MessageType=="framework.admission.rejected").PayloadJson).RootElement;
            Check(!control.GetProperty("RequestInvoked").GetBoolean(),"Unavailable trade plugin invoked business");
            Check(!a.Packets.Any(p=>p.MessageType==FrameworkTradeProtocol.CreateResponseType),"Corrupt configuration forged trade response");
            Console.WriteLine("P3 WIRE RESULT passed; phase=bad-config; plugin-unavailable/correlated-refusal/no-default-reset"); return;
        }
        if(initial)
        {
            state.Principals=new[]{a.Principal,b.Principal}; var created=Create(a,b,"wire-create"); Check(created.Success,"Initial creation refused"); state.Trade=created.TradeId;
            a.Send(new FrameworkPacket { Kind=FrameworkProtocol.KindItem, MessageType=FrameworkTradeProtocol.Capability, MessageId="wire-item", PayloadJson=FrameworkSerialization.SerializePayload(new FrameworkItemPayload { CodecId="synthetic.opaque",PayloadBytes=new byte[]{17,0,255} }) });
        }
        else Check(a.Principal==state.Principals[0] && b.Principal==state.Principals[1],"UUID changed across restart");
        var offer=new FrameworkTradeOfferUpdateRequest { TradeId=state.Trade,ItemPacketRefs=new List<string>{"wire-item"} };
        if(initial || phase=="restart")
        {
            a.Command(FrameworkTradeProtocol.OfferUpdateRequestType,offer,"wire-offer");
            Wait(a,b,()=>a.Find(FrameworkTradeProtocol.OfferUpdateResponseType,"wire-offer")!=null);
            var response=FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(a.Find(FrameworkTradeProtocol.OfferUpdateResponseType,"wire-offer").PayloadJson);
            Check(response.Success && response.Items.Single().PayloadBytes.SequenceEqual(new byte[]{17,0,255}),"Original committed offer/replay differs");
        }
        string hash=Fingerprint("offer",offer);
        var found=Lookup(a,b,"wire-offer",hash,"query-own");
        Check(found.GetProperty("Status").GetString()=="Committed" && found.GetProperty("Success").GetBoolean() && found.GetProperty("TradeId").GetString()==state.Trade && found.GetProperty("SnapshotVersion").GetInt64()==2,"Committed query differs");
        Check(Lookup(b,a,"wire-offer",hash,"query-other").GetProperty("Status").GetString()=="NotFound","Cross-principal query leaked");
        Check(Lookup(a,b,"wire-offer",new string('0',64),"query-conflict").GetProperty("Status").GetString()=="Conflict","Fingerprint mismatch accepted");
        if(phase=="restart")
        {
            b.Disconnect(); Wait(a,b,()=>a.Online.TryGetValue(state.Principals[1],out var online) && !online); b.Dispose();
            var packet=new FrameworkPacket { Kind=FrameworkProtocol.KindCommand, CommandKind=global::Phinix.Framework.FrameworkCommandKind.Request, MessageType=FrameworkTradeProtocol.StatusUpdateRequestType,MessageId="wire-cancel",PayloadJson=FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId=state.Trade,Cancelled=true }) };
            packet.SetCorrelationId("cancel-response"); a.Send(packet);
            a.Until(()=>a.Find(FrameworkTradeProtocol.StatusUpdateResponseType,"cancel-response")!=null);
            Check(FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(a.Find(FrameworkTradeProtocol.StatusUpdateResponseType,"cancel-response").PayloadJson).Success,"Cancellation refused");
            a.Until(()=>a.Packets.Any(p=>p.MessageType==FrameworkTradeProtocol.CancelledEventType));
            Check(FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(a.Packets.Single(p=>p.MessageType==FrameworkTradeProtocol.CancelledEventType).PayloadJson).Items.Single().PayloadBytes.SequenceEqual(new byte[]{17,0,255}),"Cancellation lost whole payload");
            a.Command(FrameworkTradeProtocol.CompletionAckRequestType,new FrameworkTradeCompletionAckRequest{TradeId=state.Trade,Cancelled=true},"wire-weak-ack");
            a.Until(()=>a.Find(FrameworkTradeProtocol.CompletionAckResponseType,"wire-weak-ack")!=null);
            // Prove the quota against a Ready target; a pre-Hello target must be refused
            // by expressibility admission before reaching business capacity checks.
            using var quotaPeer = new Peer(port, Guid.NewGuid().ToString("N")); Wait(a,quotaPeer,()=>quotaPeer.Ready);
            var quotaDenied = Create(a,quotaPeer,"wire-quota-denied");
            Check(!quotaDenied.Success && quotaDenied.FailureMessage.Contains("participant quota"),"Persisted one-trade config did not reach plugin");
            using var unready=new Peer(port,state.Keys[1],autoHello:false); Wait(a,unready,()=>unready.Principal==state.Principals[1]);
            var denied=Create(a,unready,"wire-denied"); Check(!denied.Success && denied.FailureReason == FrameworkTradeFailureReason.NotAcceptingTrades && denied.FailureMessage.Contains("negotiated"),"Pre-Hello target bypassed capability admission");
        }
        if(phase=="policy" || phase=="recovered")
        {
            var request=new FrameworkTradeStatusUpdateRequest { TradeId=state.Trade,Cancelled=true };
            var cancelled=Lookup(a,b,"wire-cancel",Fingerprint("status",request),"query-terminal");
            Check(cancelled.GetProperty("Status").GetString()=="Committed" && cancelled.GetProperty("Cancelled").GetBoolean(),"Terminal result lost");
            if(phase=="policy")
            {
                Wait(a,b,()=>b.Packets.Any(p=>p.MessageType==FrameworkTradeProtocol.CancelledEventType));
                var receipt=FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(b.Packets.Single(p=>p.MessageType==FrameworkTradeProtocol.CancelledEventType).PayloadJson);
                Check(receipt.TradeId==state.Trade && receipt.Items.Count==0,"Offline recipient result differs");
                Check(!Create(a,b,"wire-denied").Success,"Old rejected operation re-executed after config change");
                var enabled=Create(a,b,"wire-enabled"); Check(enabled.Success,"Explicit zero quota did not reach restarted plugin"); state.NewTrade=enabled.TradeId;
            }
            Check(!a.Packets.Any(p=>p.MessageType==FrameworkTradeProtocol.CancelledEventType),"Unknown delivery resent to original recipient");
            if(phase=="recovered")
            {
                Check(!b.Packets.Any(p=>p.MessageType==FrameworkTradeProtocol.CancelledEventType),"Attempted offline result resent after restart");
                Wait(a,b,()=>a.Packets.Any(p=>p.MessageType==FrameworkTradeProtocol.SnapshotType && FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(p.PayloadJson).Trades.Any(t=>t.TradeId==state.NewTrade)));
            }
        }
        if(initial || phase=="policy")
        {
            var options=new FileStreamOptions { Mode=initial?FileMode.CreateNew:FileMode.Truncate,Access=FileAccess.Write };
            if(initial && !OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using var stream=new FileStream(path,options); JsonSerializer.Serialize(stream,state);
        }
        Console.WriteLine("P3 WIRE RESULT passed; phase="+phase+"; real UDP/query/identity/original-result; no game ACK");
    }
    public static void RunMixed(int port, string directory)
    {
        int checks = 0;
        void Assert(bool value, string why) { Check(value, why); checks++; }
        using var peer = new Peer(port, "synthetic-p5-mixed-modern");
        peer.Until(() => peer.Ready);
        File.WriteAllText(Path.Combine(directory, "modern-ready"), "ready");
        peer.Until(() => File.Exists(Path.Combine(directory, "original-ready")));
        string original = File.ReadAllText(Path.Combine(directory, "original-ready"));
        Assert(peer.OriginalChatPackets == 1, "Bootstrap raw history unexpectedly duplicated or missing");
        peer.Command("builtin.chat.history.request", new object(), "mixed-history");
        peer.Until(() => peer.Packets.Any(p => p.MessageType == "builtin.chat.history.sync-complete"));
        Assert(peer.Packets.Where(p => p.MessageType == "builtin.chat.message").Select(p => global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(p.PayloadBytes)).Any(m => m.Message == "mix-original-start" && m.SenderUuid == original), "Shared history failed original -> Framework translation");
        var message = new global::Phinix.Framework.BuiltInChatMessagePayload { Message = "mix-modern" };
        peer.Send(new FrameworkPacket { Kind = FrameworkProtocol.KindMessage, MessageType = "builtin.chat.message", MessageId = "mix-modern-op", PayloadBytes = message.ToByteArray() });
        peer.Until(() => peer.Packets.Where(p => p.MessageType == "builtin.chat.message").Select(p => global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(p.PayloadBytes)).Any(m => m.Message == "mix-original-return"));
        var messages = peer.Packets.Where(p => p.MessageType == "builtin.chat.message").Select(p => global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(p.PayloadBytes)).ToArray();
        Assert(messages.Count(m => m.Message == "mix-modern") == 1 && messages.Count(m => m.Message == "mix-original-return" && m.SenderUuid == original) == 1, "Mixed broadcasts duplicated or attributed to wrong principal");
        Assert(peer.OriginalChatPackets == 1, "Ready Framework peer received raw chat broadcast");
        var create = new FrameworkTradeCreateRequest { OtherPartyUuid = original };
        peer.Command(FrameworkTradeProtocol.CreateRequestType, create, "mix-incompatible-create");
        peer.Until(() => peer.Find(FrameworkTradeProtocol.CreateResponseType, "mix-incompatible-create") != null);
        var refused = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(peer.Find(FrameworkTradeProtocol.CreateResponseType, "mix-incompatible-create").PayloadJson);
        Assert(!refused.Success && string.IsNullOrEmpty(refused.TradeId) && refused.FailureReason == FrameworkTradeFailureReason.NotAcceptingTrades, "Trade created toward non-negotiated original profile");
        peer.Command("trade.operation.query.request", new Query { OperationId = "mix-incompatible-create", RequestFingerprint = Fingerprint("create", create) }, "mix-query");
        peer.Until(() => peer.Find("trade.operation.query.response", "mix-query") != null);
        var lookup = JsonDocument.Parse(peer.Find("trade.operation.query.response", "mix-query").PayloadJson).RootElement;
        Assert(lookup.GetProperty("Status").GetString() == "Committed" && !lookup.GetProperty("Success").GetBoolean(), "Mixed refusal missing durable operation result");
        File.WriteAllText(Path.Combine(directory, "modern-verified"), "verified");
        peer.Until(() => peer.Online.TryGetValue(original, out bool online) && !online);
        peer.Command(FrameworkTradeProtocol.CreateRequestType, create, "mix-incompatible-create");
        peer.Until(() => peer.Packets.Count(p => p.MessageType == FrameworkTradeProtocol.CreateResponseType && p.GetCorrelationId() == "mix-incompatible-create") == 2);
        var replay = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(peer.Packets.Last(p => p.MessageType == FrameworkTradeProtocol.CreateResponseType).PayloadJson);
        Assert(!replay.Success && replay.FailureReason == refused.FailureReason && replay.FailureMessage == refused.FailureMessage, "Disconnect rewrote committed refusal");
        peer.Until(() => File.Exists(Path.Combine(directory, "original-second")) && peer.Packets.Where(p => p.MessageType == "builtin.chat.message")
            .Select(p => global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(p.PayloadBytes)).Any(m => m.Message == "mix-original-structured-ready"));
        string second = File.ReadAllText(Path.Combine(directory, "original-second"));
        var structured = new global::Phinix.Framework.BuiltInChatMessagePayload { Message = "mix-structured" };
        structured.MentionedUuids.Add(second);
        structured.ReplyToMessageId = messages.Single(m => m.Message == "mix-modern").MessageId;
        structured.ReplyToSnippet = "mix-modern";
        peer.Send(new FrameworkPacket { Kind = FrameworkProtocol.KindMessage, MessageType = "builtin.chat.message", PayloadBytes = structured.ToByteArray() });
        peer.Until(() => File.Exists(Path.Combine(directory, "original-structured-verified")) && peer.Packets.Where(p => p.MessageType == "builtin.chat.message")
            .Select(p => global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(p.PayloadBytes)).Any(m => m.Message == "mix-after-structured"));
        var kept = peer.Packets.Where(p => p.MessageType == "builtin.chat.message")
            .Select(p => global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(p.PayloadBytes)).Single(m => m.Message == "mix-structured");
        Assert(kept.MentionedUuids.SequenceEqual(new[] { second }) && kept.SenderUuid == peer.Principal && kept.ReplyToMessageId == structured.ReplyToMessageId && kept.ReplyToSnippet == "mix-modern",
            "Original text projection corrupted Framework message semantics");
        Console.WriteLine("P5 MIXED RESULT passed; checks=" + checks + "; original-native/Framework-realUDP/chat-both-directions/explicit-mention-text/trade-refusal/durable-replay; no game delivery");
    }

    private sealed class Peer : IDisposable
    {
        private readonly NetManager client;
        internal readonly object PeerGate;
        private NetPeer peer;
        private string session;
        private Exception error;
        public string Principal;
        public bool Ready, LoginFailed;
        public int OriginalChatPackets;
        public readonly Dictionary<string, bool> Online = new();
        public readonly List<FrameworkPacket> Packets = new();
        public Peer(int port, string key, bool expectLoginFailure = false, bool autoHello = true)
        {
            var listener = new EventBasedNetListener(); client = new NetManager(listener, "Phinix");
            // Same pinned backend race as the host: receive-side peer removal races
            // its logic thread. Reuse the reviewed transport guard, not any business
            // adapter or expected protocol result. Install before starting either thread.
            var guardType = typeof(Connections.NetServer).Assembly.GetType("Connections.PinnedUdpPeerSynchronization", throwOnError: true);
            var guard = guardType.GetMethod("Install", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, new object[] { client });
            PeerGate = guardType.GetProperty("PeerGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(guard);
            listener.NetworkReceiveEvent += (_, reader) =>
            {
                try
                {
                    string module = reader.GetString(); byte[] bytes = reader.GetRemainingBytes();
                    if (module == FrameworkProtocol.ModuleName)
                    {
                        var packet = FrameworkSerialization.DeserializePacket(bytes);
                        Check(packet.Version == 2 && packet.SessionId == session, "Cross-recipient envelope");
                        if (packet.MessageType == "builtin.chat.message")
                        {
                            var chat = global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(packet.PayloadBytes);
                            Check(packet.SenderUuid == chat.SenderUuid && packet.MessageId == chat.MessageId, "Chat identity envelope mismatch");
                        }
                        else Check(packet.SenderUuid == FrameworkProtocol.SystemSenderUuid, "Cross-recipient envelope");
                        if (packet.Kind == FrameworkProtocol.KindCapabilities) Ready = true;
                        Packets.Add(packet); return;
                    }
                    if (module == "Chat") { OriginalChatPackets++; return; }
                    var envelope = Any.Parser.ParseFrom(bytes);
                    if (envelope.Is(HelloPacket.Descriptor))
                    {
                        session = envelope.Unpack<HelloPacket>().SessionId;
                        Raw("Authentication", ProtobufPacketHelper.Pack(new AuthenticatePacket { SessionId = session, AuthType = AuthTypes.ClientKey, Username = key }).ToByteArray());
                    }
                    else if (envelope.Is(AuthResponsePacket.Descriptor))
                    {
                        Check(envelope.Unpack<AuthResponsePacket>().Success, "Wire authentication refused");
                        Raw("UserManagement", ProtobufPacketHelper.Pack(new LoginPacket { SessionId = session, DisplayName = "synthetic-trade-wire", AcceptingTrades = true }).ToByteArray());
                    }
                    else if (envelope.Is(UserUpdatePacket.Descriptor))
                    {
                        var update = envelope.Unpack<UserUpdatePacket>(); Online[update.User.Uuid] = update.User.LoggedIn;
                    }
                    else if (envelope.Is(LoginResponsePacket.Descriptor))
                    {
                        var login = envelope.Unpack<LoginResponsePacket>();
                        if (!login.Success) { Check(expectLoginFailure, "Wire login refused"); LoginFailed = true; return; }
                        Check(!expectLoginFailure, "Failed identity snapshot published login success"); Principal = login.Uuid;
                        Raw("UserManagement", ProtobufPacketHelper.Pack(new UserUpdatePacket { SessionId = session, Uuid = Principal,
                            User = new User { Uuid = Principal, DisplayName = "synthetic-trade-wire", AcceptingTrades = true } }).ToByteArray());
                        if (!autoHello) return;
                        Send(new FrameworkPacket { Kind = FrameworkProtocol.KindHello, PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkCapabilitiesPayload { Capabilities = new List<string>
                        { FrameworkTradeProtocol.Capability, FrameworkTradeProtocol.CreateRequestType, FrameworkTradeProtocol.CreateResponseType, FrameworkTradeProtocol.SnapshotType,
                          FrameworkTradeProtocol.OfferUpdateRequestType, FrameworkTradeProtocol.OfferUpdateResponseType, FrameworkTradeProtocol.CompletedEventType, FrameworkTradeProtocol.CancelledEventType,
                          FrameworkTradeProtocol.StatusUpdateRequestType, FrameworkTradeProtocol.StatusUpdateResponseType,
                          FrameworkTradeProtocol.CompletionAckRequestType, FrameworkTradeProtocol.CompletionAckResponseType,
                          "trade.operation.query.request", "trade.operation.query.response",
                          "builtin.chat", "builtin.chat.message", "builtin.chat.history.request", "builtin.chat.history.sync-complete" } }) });
                    }
                }
                catch (Exception failure) { error = new Exception(failure.Message == "Cross-recipient envelope" ? "Cross-recipient envelope" : "Independent Trade wire assertion failed"); }
            };
            Check(client.Start(), "Trade probe socket start"); peer = client.Connect("127.0.0.1", port);
        }
        private void Raw(string module, byte[] bytes) { var writer = new NetDataWriter(); writer.Put(module); writer.Put(bytes); lock (PeerGate) peer.Send(writer, SendOptions.ReliableOrdered); }
        public void Send(FrameworkPacket packet) { packet.SessionId = session; packet.SenderUuid = Principal; Raw(FrameworkProtocol.ModuleName, FrameworkSerialization.SerializePacket(packet)); }
        public void Command<T>(string type, T payload, string id) => Send(new FrameworkPacket { Kind = FrameworkProtocol.KindCommand, CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request, MessageType = type, MessageId = id, PayloadJson = FrameworkSerialization.SerializePayload(payload) });
        public FrameworkPacket Find(string type, string correlation) => Packets.FirstOrDefault(p => p.MessageType == type && p.GetCorrelationId() == correlation);
        public void Pump() { client.PollEvents(); if (error != null) throw error; }
        public void Until(Func<bool> done) { var t = Stopwatch.StartNew(); while (!done() && t.ElapsedMilliseconds < 10000) { Pump(); Thread.Sleep(5); } Check(done(), "P3 protocol condition deadline"); }
        public void Disconnect() { lock (PeerGate) client.DisconnectPeer(peer); }
        public void Dispose() => client.Stop();
    }
}
