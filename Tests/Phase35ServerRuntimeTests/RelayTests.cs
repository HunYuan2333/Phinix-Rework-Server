using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Connections;
using ServerRuntime;
using Utils.Framework;

internal static partial class Program
{
    private static FrameworkPacket RelayPacket(string target = null) => new FrameworkPacket
    {
        Kind = FrameworkProtocol.KindCommand, Flow = global::Phinix.Framework.FrameworkFlow.Command,
        MessageType = "request", SenderUuid = "alice", SessionId = "secret", PayloadJson = "{}",
        CommandRoute = new CommandRoute { Mode = CommandProcessingMode.Relay, PluginId = "author.research",
            CommandType = "request", Version = 1, TargetPlayerUuid = target }
    };
    private static void AssertRelayContracts()
    {
        var local = new ClientCommandCapabilities();
        var first = local.Declare("owner", "author.research", "request");
        var second = local.Declare("other", "author.research", "request");
        Assert(local.GetSnapshot().Count == 1, "Duplicate capability declarations aggregate.");
        first.Dispose(); first.Dispose();
        Assert(local.GetSnapshot().Count == 1, "Independent owner survives duplicate disposal.");
        local.RevokeOwner("other"); second.Dispose(); local.RevokeOwner("absent");
        Assert(local.GetSnapshot().Count == 0, "Last owner removal revokes declaration.");
        bool invalid = false;
        try { local.Declare("owner", "core.system", "request"); } catch (ArgumentException) { invalid = true; }
        Assert(invalid, "System namespace declarations rejected.");
        for (int i = 0; i < 256; i++) local.Declare("owner", "author.test", "c" + i);
        try { local.Declare("owner", "author.test", "overflow"); invalid = false; } catch (InvalidOperationException) { invalid = true; }
        Assert(invalid, "Local declaration budget enforced.");

        DateTime time = DateTime.UtcNow;
        var sent = new List<FrameworkPacket>();
        var registry = new ClientCommandCapabilities();
        using var declaration = registry.Declare("owner", "author.research", "request");
        using var client = new ClientCommandRelay(registry, packet => { sent.Add(packet); return true; }, () => time);
        Assert(client.SendRelay("author.research", "request", "{}").Result.Error == "UnsupportedRelay", "Old server Relay unavailable locally.");
        client.Begin("epoch");
        var snapshot = FrameworkSerialization.DeserializePayload<CommandCapabilitySnapshot>(sent.Last().PayloadJson);
        client.Acknowledge(new CommandCapabilityAck { Epoch = "old", Revision = snapshot.Revision });
        Assert(client.SyncState == CapabilitySyncState.Pending, "Old epoch ACK ignored.");
        time = time.AddSeconds(2); client.Pulse();
        Assert(sent.Count == 2, "Lost snapshot ACK retries latest state.");
        client.Acknowledge(new CommandCapabilityAck { Epoch = "epoch", Revision = snapshot.Revision });
        Assert(client.SyncState == CapabilitySyncState.Synced, "Matching ACK synchronizes state.");
        client.Acknowledge(new CommandCapabilityAck { Epoch = "epoch", Revision = snapshot.Revision, Error = "RateLimited" });
        Assert(client.SyncState == CapabilitySyncState.Synced, "Late error cannot invalidate confirmed snapshot.");
        var pending = client.SendRelay("author.research", "request", "{}");
        var outgoing = sent.Last();
        client.Complete(new RelayResult { RequestMessageId = outgoing.MessageId,
            Deliveries = new List<RelayDelivery> { new RelayDelivery { ConnectionId = "b", Outcome = "Submitted" } } });
        Assert(pending.Result.Deliveries.Single().Outcome == "Submitted", "Routing submission result correlated.");
        var uncertain = client.SendRelay("author.research", "request", "{}");
        client.Disconnect();
        Assert(uncertain.Result.Error == "Unknown", "Disconnect preserves uncertainty.");
        client.Begin("reconnected");
        Assert(FrameworkSerialization.DeserializePayload<CommandCapabilitySnapshot>(sent.Last().PayloadJson).Capabilities.Count == 1,
            "Reconnect restores declarations.");
        declaration.Dispose(); time = time.AddSeconds(2); client.Pulse();
        Assert(FrameworkSerialization.DeserializePayload<CommandCapabilitySnapshot>(sent.Last().PayloadJson).Capabilities.Count == 0,
            "Dynamic revoke produces full snapshot.");
        using var failing = new ClientCommandRelay(registry, _ => false, () => time);
        failing.Begin("e");
        for (int i = 0; i < 5; i++) { time = time.AddSeconds(10); failing.Pulse(); }
        Assert(failing.SyncState == CapabilitySyncState.Failed, "Repeated sync failure observable.");
        bool available = false;
        var recoveryPackets = new List<FrameworkPacket>();
        using var recovering = new ClientCommandRelay(registry, packet =>
        { if (!available) return false; recoveryPackets.Add(packet); return true; }, () => time);
        recovering.Begin("recovery");
        time = time.AddSeconds(2); recovering.Pulse();
        Assert(recoveryPackets.Count == 0 && recovering.SyncError == "NotSent", "Sync send failure stays observable.");
        available = true; time = time.AddSeconds(10); recovering.Pulse();
        var recoveredSnapshot = FrameworkSerialization.DeserializePayload<CommandCapabilitySnapshot>(recoveryPackets.Last().PayloadJson);
        recovering.Acknowledge(new CommandCapabilityAck { Epoch = "recovery", Revision = recoveredSnapshot.Revision });
        Assert(recovering.SyncState == CapabilitySyncState.Synced && recovering.SyncError == null, "Snapshot retry recovers after transport failure.");
        var beforeUpdate = recoveredSnapshot;
        using var dynamicDeclaration = registry.Declare("dynamic", "author.dynamic", "event");
        time = time.AddSeconds(10); recovering.Pulse();
        var afterUpdate = FrameworkSerialization.DeserializePayload<CommandCapabilitySnapshot>(recoveryPackets.Last().PayloadJson);
        recovering.Acknowledge(new CommandCapabilityAck { Epoch = "recovery", Revision = beforeUpdate.Revision });
        Assert(recovering.SyncState == CapabilitySyncState.Pending && afterUpdate.Revision > beforeUpdate.Revision, "Stale ACK cannot acknowledge dynamic update.");
        recovering.Acknowledge(new CommandCapabilityAck { Epoch = "recovery", Revision = afterUpdate.Revision });
        var timeout = recovering.SendRelay("author.dynamic", "event", "{}");
        time = time.AddSeconds(16); recovering.Pulse();
        Assert(timeout.Result.Error == "Unknown", "Lost route result ends as Unknown without replay.");
        recovering.Disconnect(); recovering.Begin("next-epoch");
        recovering.Acknowledge(new CommandCapabilityAck { Epoch = "recovery", Revision = afterUpdate.Revision });
        Assert(recovering.SyncState == CapabilitySyncState.Pending, "Old connection ACK cannot affect reconnect.");
        var oldPacket = new Utils.Framework.LegacyWire.FrameworkPacket { Kind = "command", MessageType = "trade.request", PayloadJson = "{}" };
        var newReader = FrameworkSerialization.DeserializePayload<FrameworkPacket>(FrameworkSerialization.SerializePayload(oldPacket));
        Assert(newReader.CommandRoute == null && newReader.MessageType == "trade.request", "Real frozen old packet reads as Server mode.");
        var oldReader = FrameworkSerialization.DeserializePayload<Utils.Framework.LegacyWire.FrameworkPacket>(
            FrameworkSerialization.SerializePayload(RelayPacket()));
        Assert(oldReader.MessageType == "request" && oldReader.Kind == "command", "Frozen JSON serializer tolerates additive route field.");
        var proto = new global::Phinix.Framework.FrameworkCommandPacket { CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request };
        Assert(global::Phinix.Framework.FrameworkCommandPacket.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(proto)).CommandKind == proto.CommandKind,
            "Existing inner protobuf representation round trips unchanged.");
        Assert(CommandRelayProtocol.Validate(RelayPacket()) == null, "Valid Relay accepted.");
        Assert(CommandRelayProtocol.Validate(RelayPacket("bad\0target")) == "InvalidCommand", "Control characters in player target rejected.");
        var huge = RelayPacket(); huge.PayloadJson = new string('x', 65536);
        Assert(CommandRelayProtocol.Validate(huge) == "PayloadTooLarge", "Encoded payload limit enforced.");
    }

    private sealed class DenyRelay : ICommandRelayAuthorization
    { public bool CanRelay(string principal, CommandRoute route) => false; }

    private static void AssertRelayRouting()
    {
        DateTime time = DateTime.UtcNow;
        var live = new HashSet<string> { "a", "b", "b2", "old", "empty" };
        var sessions = new ServerSessionRegistry((c, p, s) => live.Contains(c));
        var epochs = new Dictionary<string, string>();
        foreach (string id in live)
        {
            string player = id == "a" ? "alice" : id.StartsWith("b") ? "bob" : id;
            string[] caps = id == "old" ? Array.Empty<string>() : new[] { CommandRelayProtocol.Capability };
            Assert(sessions.TryBind(id, player, "secret", 2, caps, caps, out var bound, out _), "Bind Relay test connection.");
            sessions.MarkReady(bound); epochs[id] = bound.Epoch;
        }
        var router = new CommandCapabilityRouter(sessions, () => live.ToArray(),
            player => live.Any(c => sessions.TryGetReady(c, out var session) && session.PrincipalId == player), now: () => time);
        CommandCapabilitySnapshot Snapshot(string id, long revision = 1, bool empty = false) => new CommandCapabilitySnapshot
        {
            Epoch = epochs[id], Revision = revision, Capabilities = empty ? new List<CommandCapability>() :
                new List<CommandCapability> { new CommandCapability { PluginId = "author.research", CommandType = "request", Version = 1 } }
        };
        foreach (string id in new[] { "a", "b", "b2" }) Assert(router.Apply(id, Snapshot(id)).Error == null, "Capability snapshot applied.");
        var excessive = Snapshot("empty");
        excessive.Capabilities = Enumerable.Range(0, 257).Select(i => new CommandCapability { PluginId = "author.test", CommandType = "c" + i, Version = 1 }).ToList();
        Assert(router.Apply("empty", excessive).Error == "InvalidCapability", "Remote capability count bounded.");
        var badVersion = Snapshot("empty"); badVersion.Capabilities[0].Version = 0;
        Assert(router.Apply("empty", badVersion).Error == "InvalidCapability", "Invalid capability version rejected.");
        Assert(router.Apply("old", Snapshot("old")).Error == "UnsupportedRelay", "Old connection cannot declare.");
        Assert(router.Apply("b", Snapshot("b")).Error == null, "Duplicate snapshot idempotent.");
        Assert(router.Apply("b", Snapshot("b", 1, true)).Error == "InvalidCapability", "Revision collision rejected.");
        Assert(router.Apply("b", Snapshot("b", 2, true)).Error == "RateLimited", "Rapid update bounded.");
        time = time.AddSeconds(1);
        Assert(router.Apply("b", Snapshot("b", 2, true)).Error == null, "Capability revoked atomically.");
        Assert(router.Apply("b", Snapshot("b")).Revision == 2, "Older snapshot cannot resurrect revoked capability.");
        Assert(!router.Eligible("b", RelayPacket()), "Revoked client excluded.");
        var versionTwo = RelayPacket(); versionTwo.CommandRoute.Version = 2;
        Assert(!router.Eligible("b2", versionTwo), "Capability versions match exactly.");
        time = time.AddSeconds(1); router.Apply("b", Snapshot("b", 3));
        var delivered = new List<string>();
        PacketDispatchResult Deliver(FrameworkPacket packet, string[] targets)
        { delivered.AddRange(targets); return new PacketDispatchResult(targets.ToDictionary(id => id, _ => TransportSendOutcome.Submitted)); }
        var broadcast = router.Route("a", "alice", epochs["a"], RelayPacket(), Deliver);
        Assert(broadcast.Error == null && delivered.OrderBy(x => x).SequenceEqual(new[] { "b", "b2" }), "Broadcast filters capabilities and excludes sender.");
        delivered.Clear();
        var unicast = router.Route("a", "alice", epochs["a"], RelayPacket("bob"), Deliver);
        Assert(unicast.Deliveries.Count == 2 && delivered.Count == 2, "Player unicast reaches every eligible connection once.");
        Assert(router.Route("a", "alice", epochs["a"], RelayPacket("empty"), Deliver).Error == "NoCapability", "Missing declaration has explicit error.");
        Assert(router.Route("a", "alice", epochs["a"], RelayPacket("absent"), Deliver).Error == "TargetOffline", "Offline target reported.");
        Assert(router.Route("a", "alice", epochs["a"], RelayPacket("old"), Deliver).Error == "UnsupportedRelay", "Old target reported.");
        Assert(router.Validate("a", "forged", epochs["a"], RelayPacket()) == "NotAuthenticated", "Principal spoof rejected.");
        router.Authorization = new DenyRelay();
        Assert(router.Route("a", "alice", epochs["a"], RelayPacket(), Deliver).Error == "Forbidden", "Routing policy enforced.");
        router.Authorization = null;
        var partial = router.Route("a", "alice", epochs["a"], RelayPacket(), (packet, targets) =>
            new PacketDispatchResult(new Dictionary<string, TransportSendOutcome> { ["b"] = TransportSendOutcome.Submitted, ["b2"] = TransportSendOutcome.Unknown }));
        Assert(partial.Error == "DeliveryFailed" && partial.Deliveries.Any(d => d.Outcome == "Unknown"), "Partial submission preserves uncertainty.");
        for (int i = 0; i < 20; i++) Assert(router.Admit("a") == null, "Burst budget permits bounded commands.");
        Assert(router.Admit("a") == "RateLimited", "Command rate bounded.");
        int denied = 0;
        for (int i = 0; i < 100000; i++) if (router.Admit("a") == "RateLimited") denied++;
        Assert(denied == 100000, "Sustained same-tick flood cannot exceed exhausted sender budget.");
        time = time.AddSeconds(1);
        Assert(router.Admit("a") == null, "Rate budget recovers without reconnect after flood.");
        int controlAccepted = 0;
        for (int i = 0; i < 100000; i++) if (router.AdmitControl("b")) controlAccepted++;
        Assert(controlAccepted == 4, "Control flood is bounded independently of business commands.");
        router.Remove("b"); live.Remove("b"); live.Remove("b2");
        Assert(router.Route("a", "alice", epochs["a"], RelayPacket(), Deliver).Error == "NoRecipients", "No receivers reported explicitly.");
        var limited = new CommandCapabilityRouter(sessions, () => live.ToArray(), _ => true,
            new RelayLimits { BurstFanoutBytes = 1 }, () => time);
        limited.Apply("a", Snapshot("a")); limited.Apply("empty", Snapshot("empty"));
        Assert(limited.Route("a", "alice", epochs["a"], RelayPacket(), Deliver).Error == "RateLimited", "Fanout byte budget checked before delivery.");
        sessions.Remove("a");
        Assert(router.Validate("a", "alice", epochs["a"], RelayPacket()) == "NotAuthenticated", "Disconnected sender rejected.");
        sessions.TryBind("a", "alice", "new-secret", 2, new[] { CommandRelayProtocol.Capability }, new[] { CommandRelayProtocol.Capability }, out var reconnected, out _);
        sessions.MarkReady(reconnected);
        Assert(router.Apply("a", Snapshot("a")).Error == "InvalidCapability", "Old epoch snapshot cannot affect reconnect.");
        Assert(!router.Eligible("a", RelayPacket()), "Old connection declaration not inherited by new epoch.");
    }

    private static void AssertRelayPipeline()
    {
        var harness = new PipelineTestHarness();
        int defaults = 0, routed = 0, observed = 0;
        harness.DefaultCommandHandlers.Add(new DelegateServerDefaultCommandHandler(_ => true, (_, __) =>
        { defaults++; return new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Handled }; }));
        harness.CommandObservers.Add(new DelegateServerCommandObserver(_ => true, (_, __, ___) => observed++));
        RelayResult Route(FrameworkPacket p) { routed++; return new RelayResult { RequestMessageId = p.MessageId }; }
        var result = harness.Runner.ProcessCommand(RelayPacket(), CreateServerContext(), CommandRelayProtocol.Validate, Route);
        Assert(result.Consumed && routed == 1 && defaults == 0 && observed == 1, "Relay uses unified interception and observation without default business execution.");
        harness.InboundCommandInterceptors.Add(new DelegateServerInboundCommandInterceptor(_ => true, (packet, _) =>
        { packet.CommandRoute.PluginId = "core.forged"; return new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Continue }; }));
        result = harness.Runner.ProcessCommand(RelayPacket(), CreateServerContext(), CommandRelayProtocol.Validate, Route);
        Assert(result.Relay.Error == "InvalidCommand" && routed == 1, "In-place mutation revalidated.");
        harness.InboundCommandInterceptors.Clear();
        foreach (var action in new[] { MessageHandlingResultAction.Handled, MessageHandlingResultAction.StopPropagation })
        {
            harness.InboundCommandInterceptors.Clear();
            harness.InboundCommandInterceptors.Add(new DelegateServerInboundCommandInterceptor(_ => true,
                (_, __) => new ServerIncomingCommandResult { Action = action }));
            result = harness.Runner.ProcessCommand(RelayPacket(), CreateServerContext(), CommandRelayProtocol.Validate, Route);
            Assert(routed == 1 && result.Consumed, "Terminal interceptor prevents routing.");
        }
        harness.InboundCommandInterceptors.Clear();
        harness.InboundCommandInterceptors.Add(new DelegateServerInboundCommandInterceptor(_ => true, (packet, _) =>
            new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Replace, Command = RelayPacket("bob") }));
        result = harness.Runner.ProcessCommand(RelayPacket(), CreateServerContext(), CommandRelayProtocol.Validate, Route);
        Assert(routed == 2 && result.Command.CommandRoute.TargetPlayerUuid == "bob", "Valid replacement reaches Router.");
        harness.InboundCommandInterceptors.Clear();
        harness.InboundCommandInterceptors.Add(new DelegateServerInboundCommandInterceptor(_ => throw new Exception(), (_, __) => null));
        result = harness.Runner.ProcessCommand(RelayPacket(), CreateServerContext(), CommandRelayProtocol.Validate, Route);
        Assert(result.Relay.Error == "ProcessingFailed" && routed == 2, "Throwing interceptor fails closed.");
    }
}
