using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Authentication;
using Connections;
using PhinixServer.Framework;
using Utils.Framework;
using UserManagement;

internal static partial class ReadyNetworkTests
{
    private sealed class NoRelayPlugins : IExtensionDiscoveryPolicy
    {
        public bool ShouldScanAssembly(Assembly assembly) => false;
        public bool ShouldDiscoverType(Type type) => false;
    }
    public static void RunRelay()
    {
        using var net = new NetServer(new IPEndPoint(IPAddress.Any, 0), 8);
        var auth = new ServerAuthenticator(net, "synthetic", "synthetic", AuthTypes.ClientKey);
        var users = new ServerUserManager(net, auth, 100);
        var context = new ExtensionHostContext();
        context.AddService<IExtensionDiscoveryPolicy>(new NoRelayPlugins());
        var framework = new PhinixFrameworkServer(net, auth, users, context);
        net.Start();
        try
        {
            using var a = new Peer(net.LocalPort, "relay-alice-" + Guid.NewGuid().ToString("N"));
            using var b = new Peer(net.LocalPort, "relay-bob-" + Guid.NewGuid().ToString("N"));
            using var old = new Peer(net.LocalPort, "relay-old-" + Guid.NewGuid().ToString("N"));
            a.PumpUntil(() => a.Principal != null); b.PumpUntil(() => b.Principal != null); old.PumpUntil(() => old.Principal != null);
            a.Hello(2, CommandRelayProtocol.Capability); b.Hello(2, CommandRelayProtocol.Capability); old.Hello(2, "core.command-pipeline");
            a.PumpUntil(() => a.Framework.Any(p => p.Kind == "capabilities"));
            b.PumpUntil(() => b.Framework.Any(p => p.Kind == "capabilities"));
            old.PumpUntil(() => old.Framework.Any(p => p.Kind == "capabilities"));
            string Epoch(Peer peer) => peer.Framework.First(p => p.Kind == "capabilities").TryGetMetadataValue(CommandRelayProtocol.EpochMetadata, out var e) ? e : null;
            var declarations = new ClientCommandCapabilities();
            using var declared = declarations.Declare("research", "author.research", "request");
            var empty = new ClientCommandCapabilities();
            a.SendFramework(new FrameworkPacket { Kind = CommandRelayProtocol.SnapshotKind,
                PayloadJson = FrameworkSerialization.SerializePayload(empty.Capture(Epoch(a))) });
            b.SendFramework(new FrameworkPacket { Kind = CommandRelayProtocol.SnapshotKind,
                PayloadJson = FrameworkSerialization.SerializePayload(declarations.Capture(Epoch(b))) });
            a.PumpUntil(() => a.Framework.Any(p => p.Kind == CommandRelayProtocol.AckKind));
            b.PumpUntil(() => b.Framework.Any(p => p.Kind == CommandRelayProtocol.AckKind));
            Check(FrameworkSerialization.DeserializePayload<CommandCapabilityAck>(b.Framework.Last().PayloadJson).Error == null, "Network capability sync failed");
            FrameworkPacket Command(string target = null) => new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindCommand, Flow = global::Phinix.Framework.FrameworkFlow.Command,
                MessageType = "request", PayloadJson = "opaque research data",
                CommandRoute = new CommandRoute { Mode = CommandProcessingMode.Relay, PluginId = "author.research",
                    CommandType = "request", Version = 1, TargetPlayerUuid = target }
            };
            var command = Command(); a.SendFramework(command);
            a.PumpUntil(() => a.Framework.Any(p => p.Kind == CommandRelayProtocol.ResultKind));
            b.PumpUntil(() => b.Framework.Any(p => p.MessageId == command.MessageId));
            var received = b.Framework.Single(p => p.MessageId == command.MessageId);
            Check(received.SenderUuid == a.Principal && received.SessionId == b.Session && received.PayloadJson == command.PayloadJson,
                "Relay identity, recipient session or opaque payload changed");
            var result = FrameworkSerialization.DeserializePayload<RelayResult>(a.Framework.Last().PayloadJson);
            Check(result.Error == null && result.Deliveries.Count == 1 && result.Deliveries[0].Outcome == "Submitted", "Submission result missing");
            old.PumpFor(100);
            Check(!old.Framework.Any(p => p.MessageId == command.MessageId), "Old client received incompatible Relay");
            var unsupported = Command(old.Principal); a.SendFramework(unsupported);
            a.PumpUntil(() => a.Framework.Any(p => p.Kind == CommandRelayProtocol.ResultKind &&
                FrameworkSerialization.DeserializePayload<RelayResult>(p.PayloadJson).RequestMessageId == unsupported.MessageId));
            Check(FrameworkSerialization.DeserializePayload<RelayResult>(a.Framework.Last().PayloadJson).Error == "UnsupportedRelay", "Old target error missing");
            var noCapability = Command(a.Principal); a.SendFramework(noCapability);
            a.PumpUntil(() => a.Framework.Any(p => p.Kind == CommandRelayProtocol.ResultKind &&
                FrameworkSerialization.DeserializePayload<RelayResult>(p.PayloadJson).RequestMessageId == noCapability.MessageId));
            Check(FrameworkSerialization.DeserializePayload<RelayResult>(a.Framework.Last().PayloadJson).Error == "NoCapability", "Undeclared target error missing");
            Console.WriteLine("RELAY NETWORK RESULT passed; real UDP/auth/login/negotiation/snapshot/broadcast/identity/recipient-session/old-peer/explicit-errors; no server business plugin");
        }
        finally { net.Stop(); framework.ShutdownExtensions(); auth.Stop(); }
    }
}
