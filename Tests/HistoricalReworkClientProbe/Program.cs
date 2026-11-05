using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Linq;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using Authentication;
using Connections;
using UserManagement;

// Separate Mono process loading only the fixed, historical client/Common assemblies.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        try
        {
            using (var net = new NetClient())
            using (var auth = new ClientAuthenticator(net, (session, name, description, type, callback) => callback(false, session, type, "", ""), args[1]))
            using (var authenticated = new ManualResetEventSlim())
            using (var loggedIn = new ManualResetEventSlim())
            using (var synced = new ManualResetEventSlim())
            {
                var users = new ClientUserManager(net, auth);
                int publicPackets = 0, privateFields = 0;
                var handlers = (Dictionary<string, NetCommon.PacketHandlerDelegate>)typeof(NetCommon)
                    .GetField("registeredPacketHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(net);
                var original = handlers["UserManagement"];
                handlers["UserManagement"] = (module, connection, bytes) =>
                {
                    var envelope = Any.Parser.ParseFrom(bytes);
                    IEnumerable<User> received = null;
                    if (envelope.TypeUrl == "Phinix/UserManagement.UserSyncPacket")
                        received = envelope.Unpack<UserSyncPacket>().Users;
                    else if (envelope.TypeUrl == "Phinix/UserManagement.UserUpdatePacket")
                        received = new[] { envelope.Unpack<UserUpdatePacket>().User };
                    if (received != null)
                    {
                        foreach (var user in received)
                        {
                            var allowed = new User { Uuid = user.Uuid, DisplayName = user.DisplayName,
                                LoggedIn = user.LoggedIn, AcceptingTrades = user.AcceptingTrades };
                            if (!user.ToByteArray().SequenceEqual(allowed.ToByteArray()))
                                Interlocked.Increment(ref privateFields);
                        }
                        Interlocked.Increment(ref publicPackets);
                    }
                    original(module, connection, bytes);
                };
                auth.OnAuthenticationSuccess += (_, __) => authenticated.Set();
                users.OnLoginSuccess += (_, __) => loggedIn.Set();
                users.OnUserSync += (_, __) => synced.Set();
                net.Connect(new IPEndPoint(IPAddress.Loopback, int.Parse(args[0])));
                if (!authenticated.Wait(5000)) throw new InvalidOperationException("auth deadline");
                // Historical API omits AcceptingTrades; do not add a modern field/default.
                users.SendLogin("synthetic-historical-rework");
                if (!loggedIn.Wait(5000) || !synced.Wait(5000)) throw new InvalidOperationException("login/sync deadline");
                var self = users.GetUsers().Single(u => u.Uuid == users.Uuid);
                if (self.DisplayName != "synthetic-historical-rework" || !self.LoggedIn || string.IsNullOrEmpty(users.Uuid))
                    throw new InvalidOperationException("public self projection");
                if (publicPackets == 0 || privateFields != 0) throw new InvalidOperationException("private user wire fields");
                if (!File.Exists(args[1]) || new FileInfo(args[1]).Length == 0) throw new InvalidOperationException("credential persistence");
                Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                    profile = "HistoricalRework", passed = true, uuid = users.Uuid,
                    automaticAuthentication = true, publicUserSync = true, secretFreePublicSync = true, credentialFilePersisted = true,
                    reliableAssetAck = false
                }));
            }
            return 0;
        }
        catch (Exception error)
        {
            // Do not emit exception messages: historical auth may include bearer material.
            Console.Error.WriteLine("HISTORICAL REWORK failed: " + error.GetType().Name);
            return 1;
        }
    }
}
