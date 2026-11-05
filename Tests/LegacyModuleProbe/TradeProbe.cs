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
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LiteNetLib;
using Trading;
using UserManagement;
using Utils;

// Runs only historical modules. No modern adapter, gameplay or completion ACK oracle.
internal static class TradeProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly object Gate = new object();
    private static readonly List<object> Captures = new List<object>();
    private static string scenario;
    private static int captureErrors;

    private static string Hash(byte[] bytes)
    {
        using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(bytes).Select(b => b.ToString("x2")));
    }

    private static void Tap(NetCommon net, string direction, string peer)
    {
        var handlers = (Dictionary<string, NetCommon.PacketHandlerDelegate>)typeof(NetCommon).GetField("registeredPacketHandlers", Private).GetValue(net);
        var original = handlers["Trading"];
        handlers["Trading"] = (module, connection, bytes) =>
        {
            try
            {
                var envelope = Any.Parser.ParseFrom(bytes);
                IMessage sanitized = null;
                string transformation = null;
                if (envelope.TypeUrl == "Phinix/Trading.UpdateTradeItemsPacket")
                {
                    var packet = envelope.Unpack<UpdateTradeItemsPacket>();
                    if (direction == "client-to-original-server") packet.Uuid = "SENDER";
                    packet.SessionId = ""; packet.TradeId = "TRADE";
                    sanitized = packet;
                    transformation = "Remove SessionId; normalize request Uuid and TradeId; preserve token and both item lists";
                }
                else if (envelope.TypeUrl == "Phinix/Trading.UpdateTradeItemsResponsePacket")
                {
                    var packet = envelope.Unpack<UpdateTradeItemsResponsePacket>();
                    packet.TradeId = "TRADE";
                    sanitized = packet;
                    transformation = "Normalize TradeId; preserve actual Success, Token and Items";
                }
                if (sanitized != null)
                {
                    var derived = ProtobufPacketHelper.Pack(sanitized).ToByteArray();
                    lock (Gate) Captures.Add(new {
                        scenario, direction, peer, module, typeUrl = envelope.TypeUrl,
                        classification = "redacted-derived-from-original-module-loopback-capture",
                        rawSha256 = Hash(bytes), derivedSha256 = Hash(derived),
                        derivedAnyBase64 = Convert.ToBase64String(derived), transformation
                    });
                }
            }
            catch { Interlocked.Increment(ref captureErrors); }
            original(module, connection, bytes);
        };
    }

    private sealed class Peer : IDisposable
    {
        internal readonly NetClient Net = new NetClient();
        internal readonly ClientAuthenticator Auth;
        internal readonly ClientUserManager Users;
        internal readonly ClientTrading Trades;
        internal readonly ManualResetEventSlim Created = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim Updated = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim Synced = new ManualResetEventSlim();
        private bool disposed;
        internal string TradeId;
        internal string LastToken;
        internal Peer(string directory, string label)
        {
            Auth = new ClientAuthenticator(Net, (session, name, description, type, callback) => callback(false, session, type, "", ""),
                                           Path.Combine(directory, label + ".bin"));
            Users = new ClientUserManager(Net, Auth);
            Trades = new ClientTrading(Net, Auth, Users);
            Trades.OnTradesSynced += (_, e) => { TradeId = Trades.GetTradeIds().Single(); Synced.Set(); };
            Trades.OnTradeCreationSuccess += (_, e) => { TradeId = e.TradeId; Created.Set(); };
            Trades.OnTradeUpdateSuccess += (_, e) => { LastToken = e.Token; Updated.Set(); };
            Trades.OnTradeUpdateFailure += (_, __) => Interlocked.Increment(ref captureErrors);
            Tap(Net, "original-server-to-client", label);
        }
        internal void Connect(int port, string label)
        {
            using (var auth = new ManualResetEventSlim())
            using (var login = new ManualResetEventSlim())
            {
                Auth.OnAuthenticationSuccess += (_, __) => auth.Set();
                Users.OnLoginSuccess += (_, __) => login.Set();
                Net.Connect(new IPEndPoint(IPAddress.Loopback, port));
                Require(auth.Wait(5000), "automatic authentication");
                Users.SendLogin("synthetic-trade-" + label);
                Require(login.Wait(5000), "login");
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Net.Disconnect();
            ((System.Timers.Timer)typeof(ClientAuthenticator).GetField("sessionExtendTimer", Private).GetValue(Auth)).Dispose();
            Created.Dispose(); Updated.Dispose(); Synced.Dispose();
        }
    }

    private static void Require(bool condition, string step)
    {
        if (!condition) throw new InvalidOperationException("Original trade assertion: " + step);
    }

    private static void AssertOffer(Peer peer, string owner, params ProtoThing[] expected)
    {
        Require(peer.Trades.TryGetItemsOnOffer(peer.TradeId, owner, out var actual), "offer exists");
        Require(actual.SequenceEqual(expected), "recipient-relative exact offer");
    }

    private static void Offer(string name, Peer sender, Peer other, params ProtoThing[] items)
    {
        scenario = name;
        sender.Updated.Reset(); other.Updated.Reset();
        sender.Trades.UpdateItems(sender.TradeId, items, name);
        Require(sender.Updated.Wait(5000) && other.Updated.Wait(5000), "both actual client update handlers");
        Require(sender.LastToken == name && other.LastToken == "", "correlated result versus snapshot");
        AssertOffer(sender, sender.Users.Uuid, items);
        AssertOffer(other, sender.Users.Uuid, items);
    }

    internal static int Run(string output)
    {
        string directory = Path.Combine(Path.GetTempPath(), "phinix-old-trade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        NetServer server = null;
        ServerAuthenticator auth = null;
        try
        {
            server = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 2);
            auth = new ServerAuthenticator(server, "Synthetic original", "Isolated trade probe", AuthTypes.ClientKey);
            var users = new ServerUserManager(server, auth);
            var trading = new ServerTrading(server, auth, users);
            Tap(server, "client-to-original-server", "sender");
            server.Start();
            int port = ((NetManager)typeof(NetServer).GetField("server", Private).GetValue(server)).LocalPort;
            Require(port != 0, "bound ephemeral port");
            using (var a = new Peer(directory, "A"))
            using (var b = new Peer(directory, "B"))
            {
                a.Connect(port, "A"); b.Connect(port, "B");
                a.Trades.CreateTrade(b.Users.Uuid);
                Require(a.Created.Wait(5000) && b.Created.Wait(5000), "both actual create handlers");
                Require(a.TradeId == b.TradeId && !string.IsNullOrEmpty(a.TradeId), "same trade");
                var steel = new ProtoThing { DefName = "Steel", StackCount = 7, HitPoints = 60 };
                var wood = new ProtoThing { DefName = "WoodLog", StackCount = 3, HitPoints = 25 };
                Offer("A-offer", a, b, steel);
                AssertOffer(a, b.Users.Uuid); AssertOffer(b, b.Users.Uuid);
                Offer("B-offer", b, a, wood);
                AssertOffer(a, a.Users.Uuid, steel); AssertOffer(b, a.Users.Uuid, steel);
                string originalUuid = b.Users.Uuid;
                string originalTradeId = b.TradeId;
                b.Dispose();
                using (var resumed = new Peer(directory, "B"))
                {
                    resumed.Connect(port, "B");
                    Require(resumed.Synced.Wait(5000), "active trade sync after client module recreation");
                    Require(resumed.Users.Uuid == originalUuid && resumed.TradeId == originalTradeId, "stable identity and trade after reconnect");
                    AssertOffer(resumed, a.Users.Uuid, steel);
                    AssertOffer(resumed, resumed.Users.Uuid, wood);
                    Offer("A-clear", a, resumed);
                    AssertOffer(a, resumed.Users.Uuid, wood); AssertOffer(resumed, resumed.Users.Uuid, wood);
                }
                Require(captureErrors == 0, "no capture or client failure");
                lock (Gate)
                {
                    Require(Captures.Count == 9, "three request/result/snapshot sequences");
                    File.WriteAllText(output, new JavaScriptSerializer().Serialize(new {
                        schema = 1, sourceSha = "ce82b8f2cd57718f05de86458eebd3320f35c4d8",
                        protobufSha = "dd06cc6c8f906b760ef224a085f45e7694dc426a", profile = "Original",
                        fixtures = Captures, assertions = new[] { "two automatic identities", "same created trade", "token-correlated real offer results", "recipient-relative item lists", "client module recreation with persisted credentials restores same UUID/trade/both offers", "empty offer clears only sender" },
                        limitations = "Original module UDP capture; no upgraded Server, durability, completion ACK or game asset proof"
                    }));
                }
            }
            Console.WriteLine("LEGACY TRADE RESULT passed; captures=9; realUDP=true; recreatedClientRestored=true; reliableAssetAck=false");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("LEGACY TRADE RESULT failed: " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
        finally
        {
            server?.Stop();
            if (auth != null) ((System.Timers.Timer)typeof(ServerAuthenticator).GetField("sessionCleanupTimer", Private).GetValue(auth)).Dispose();
            Directory.Delete(directory, true);
        }
    }
}
