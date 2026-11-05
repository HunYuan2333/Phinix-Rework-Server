using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using ServerReferenceModel;
using UserManagement;
using Utils.Framework;

internal static class Program
{
    private const int Cases = 256;
    private static ulong seed = 20261015, gamma = 20261017;
    private static Failure lastFailure;
    private static int[] lastInput;
    private sealed record Failure(int FailingStep, string Reason, Step[] Trace);

    private static int Main(string[] args)
    {
        string selected = "all";
        int[] replay = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--property") selected = args[++i];
            else if (args[i] == "--seed") { var parts = args[++i].Split(','); seed = ulong.Parse(parts[0]); gamma = ulong.Parse(parts[1]); }
            else if (args[i] == "--trace") replay = JsonSerializer.Deserialize<int[]>(args[++i]);
            else if (args[i] == "--framework-negative-control") return FrameworkNegativeControl();
            else throw new ArgumentException("Unknown model-test argument");
        }
        var properties = new Dictionary<string, Func<int[], Step[]>>
        {
            ["batch"] = Batch, ["scope"] = Scope, ["sequence"] = Sequence
        };
        if (selected != "all" && !properties.ContainsKey(selected)) throw new ArgumentException("Unknown property");
        int failed = 0;
        foreach (var property in properties.Where(p => selected == "all" || selected == p.Key))
        {
            lastFailure = null;
            lastInput = null;
            string error = null;
            try
            {
                if (replay != null)
                {
                    lastInput = replay;
                    if (!Compare(property.Value(replay))) throw new InvalidOperationException("Trace mismatch");
                }
                else Prop.ForAll<int[]>(input =>
                {
                    bool matched = Compare(property.Value(input));
                    if (!matched) lastInput = input.ToArray();
                    return matched;
                }).Check(Configuration());
            }
            catch (Exception failure) { error = failure.Message; failed++; }
            Failure minimalFailure = lastFailure;
            bool? replayVerified = error != null && lastInput != null
                ? !Compare(property.Value(lastInput)) && JsonSerializer.Serialize(minimalFailure) == JsonSerializer.Serialize(lastFailure)
                : null;
            int? executedCases = replay != null ? 1 : error == null ? Cases : null;
            if (error != null && Regex.Match(error, @"after (\d+) tests?") is { Success: true } match)
                executedCases = int.Parse(match.Groups[1].Value);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                id = "TQ01-" + property.Key, layer = "L1-model/L2-component", passed = error == null,
                framework = typeof(Prop).Assembly.FullName, maxCases = Cases, executedCases,
                seed = new[] { seed, gamma }, error, minimalInput = error == null ? null : lastInput,
                minimalFailure = error == null ? null : minimalFailure, replayVerified
            }));
        }
        Console.WriteLine($"MODEL RESULT failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static Config Configuration() => Config.QuickThrowOnFailure.WithMaxTest(Cases).WithEndSize(48)
        .WithReplay(FSharpOption<Replay>.Some(new Replay(new Rnd(seed, gamma), null)));

    private static int FrameworkNegativeControl()
    {
        string Falsify()
        {
            try { Prop.ForAll<int[]>(xs => xs.Length < 2).Check(Configuration()); }
            catch (Exception failure) { return failure.Message; }
            return null;
        }
        string first = Falsify(), second = Falsify();
        // This intentionally false property must fail reproducibly and show shrinking evidence.
        bool detected = first != null && first == second && first.Contains("shrink", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine(JsonSerializer.Serialize(new { id = "TQ00-FsCheck-negative-control", passed = detected, seed = new[] { seed, gamma }, first }));
        return detected ? 0 : 1;
    }

    private static int Small(int value, int count) => (int)((uint)value % (uint)count);

    private static Step[] Batch(int[] input)
    {
        var steps = new List<Step>();
        for (int i = 0; i < 4; i++) steps.Add(new Step(StepKind.Cache, 0, i, 10 + i));
        steps.Add(new Step(StepKind.AcceptFirstParty, 0));
        int[] refs = input.Take(12).Select(value => Small(value, 6) - 1).ToArray();
        steps.Add(new Step(StepKind.Offer, 0, References: refs));
        // A subsequent offer checks availability, not just that the failed request returned false.
        for (int i = 0; i < 4; i++) steps.Add(new Step(StepKind.Offer, 0, References: new[] { i }));
        return steps.ToArray();
    }

    private static Step[] Scope(int[] input)
    {
        var steps = new List<Step>();
        foreach (int value in input.Take(24).DefaultIfEmpty(0))
        {
            int id = Small(value, 8), asset = Small(value, 100);
            steps.Add(new Step(StepKind.Cache, 0, id, asset));
            steps.Add(new Step(StepKind.Cache, 1, id, asset + 100));
            steps.Add(new Step(StepKind.Offer, 2, References: new[] { id }));
            steps.Add(new Step(StepKind.Offer, 0, References: new[] { id }));
            steps.Add(new Step(StepKind.Reconnect, 1));
            steps.Add(new Step(StepKind.Offer, 1, References: new[] { id }));
            steps.Add(new Step(StepKind.Cache, 1, id, asset + 200));
            steps.Add(new Step(StepKind.Offer, 1, References: new[] { id }));
        }
        return steps.ToArray();
    }

    private static Step[] Sequence(int[] input)
    {
        var steps = new List<Step>
        {
            new(StepKind.Cache, 0, 0, 7),
            new(StepKind.AcceptFirstParty, 0),
            new(StepKind.Offer, 0, References: new[] { 0, -1 }),
            new(StepKind.Offer, 0, References: new[] { 0 }),
            new(StepKind.Reconnect, 0),
            new(StepKind.Offer, 0, References: new[] { 0 })
        };
        foreach (int value in input.Take(48))
        {
            int actor = Small(value >> 4, 3), id = Small(value >> 8, 8);
            steps.Add(Small(value, 7) switch
            {
                0 or 1 => new Step(StepKind.Cache, actor, id, Small(value >> 12, 256)),
                2 => new Step(StepKind.Offer, actor, References: new[] { id }),
                3 => new Step(StepKind.Offer, actor, References: new[] { id, Small(value >> 16, 9) }),
                4 => new Step(StepKind.Offer, actor, References: new[] { id }, TargetExists: false),
                5 => new Step(StepKind.AcceptFirstParty, 0),
                _ => new Step(StepKind.Reconnect, actor)
            });
        }
        return steps.ToArray();
    }

    private static bool Compare(Step[] trace)
    {
        // Every shrink/replay gets a new model AND real service; no state leaks between candidates.
        var model = new OfferModel();
        var subject = new Subject();
        for (int i = 0; i < trace.Length; i++)
        {
            try
            {
                bool? expected = model.Apply(trace[i]);
                bool? actual = subject.Apply(trace[i]);
                if (expected != actual) throw new InvalidOperationException("Operation acceptance disagreed with independent model");
                subject.AssertSnapshot(model);
            }
            catch (Exception failure)
            {
                lastFailure = new Failure(i, failure.Message, trace.Take(i + 1).ToArray());
                return false;
            }
        }
        return true;
    }

    private sealed class Subject
    {
        private readonly PhinixFrameworkTradeServerService service = new(new Directory());
        private readonly List<FrameworkPacket> sent = new();
        private readonly int[] sessions = new int[3];
        private readonly string trade;
        private int correlationSequence;
        public Subject()
        {
            service.HandleCreateRequest(Packet(new FrameworkTradeCreateRequest { OtherPartyUuid = "party-1" }), Context(0));
            trade = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(sent.First(p => p.MessageType == FrameworkTradeProtocol.CreateResponseType).PayloadJson).TradeId;
            if (string.IsNullOrEmpty(trade)) throw new InvalidOperationException("Synthetic trade setup failed");
        }
        private ServerFrameworkContext Context(int actor) => new()
        {
            SenderUuid = "party-" + actor, ConnectionId = "connection-" + actor,
            SessionId = "synthetic-session-" + actor + "-" + sessions[actor], SendMessage = (_, packet) => sent.Add(packet)
        };
        private FrameworkPacket Packet<T>(T payload)
        {
            var packet = new FrameworkPacket { PayloadJson = FrameworkSerialization.SerializePayload(payload) };
            packet.SetCorrelationId("synthetic-operation-" + correlationSequence++);
            return packet;
        }
        private static string Ref(int id) => id < 0 ? "" : "reference-" + id;
        public bool? Apply(Step step)
        {
            sent.Clear();
            switch (step.Kind)
            {
                case StepKind.Reconnect: sessions[step.Actor]++; return null;
                case StepKind.Cache:
                    return service.TryCacheItemPacket(Ref(step.Reference), new FrameworkItemPayload
                    {
                        CodecId = "model.opaque-v1", PayloadBytes = BitConverter.GetBytes(step.Asset),
                        Metadata = new List<FrameworkMetadataEntry> { new() { Key = "lineage", Value = "synthetic-asset-" + step.Asset } }
                    }, Context(step.Actor));
                case StepKind.AcceptFirstParty:
                    service.HandleSnapshotRequest(Context(0));
                    long observedVersion = FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(sent.Single().PayloadJson).Trades.Single().SnapshotVersion;
                    sent.Clear();
                    var consent = Packet(new FrameworkTradeStatusUpdateRequest { TradeId = trade, Accepted = true });
                    consent.SetSnapshotVersion(observedVersion);
                    service.HandleStatusUpdateRequest(consent, Context(0));
                    return FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(sent.First(p => p.MessageType == FrameworkTradeProtocol.StatusUpdateResponseType).PayloadJson).Success;
                case StepKind.Offer:
                    service.HandleOfferUpdateRequest(Packet(new FrameworkTradeOfferUpdateRequest
                    {
                        TradeId = step.TargetExists ? trade : "unknown-trade", ItemPacketRefs = step.References.Select(Ref).ToList()
                    }), Context(step.Actor));
                    return FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(sent.Single(p => p.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType).PayloadJson).Success;
                default: throw new InvalidOperationException("Unknown trace operation");
            }
        }
        public void AssertSnapshot(OfferModel model)
        {
            sent.Clear();
            service.HandleSnapshotRequest(Context(0));
            var snapshot = FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(sent.Single().PayloadJson).Trades.Single();
            if (snapshot.SnapshotVersion != model.Version) throw new InvalidOperationException("Trade version changed unexpectedly");
            for (int actor = 0; actor < 2; actor++)
            {
                var participant = snapshot.Participants.Single(p => p.Uuid == "party-" + actor);
                if (participant.Accepted != model.Accepted[actor] || participant.ItemsOnOffer.Count != model.Offers[actor].Length)
                    throw new InvalidOperationException("Offer/acceptance state disagreed with model");
                for (int i = 0; i < model.Offers[actor].Length; i++)
                {
                    int asset = model.Offers[actor][i];
                    var item = participant.ItemsOnOffer[i];
                    if (item.CodecId != "model.opaque-v1" || !item.PayloadBytes.SequenceEqual(BitConverter.GetBytes(asset)) ||
                        item.Metadata.Count != 1 || item.Metadata[0].Key != "lineage" || item.Metadata[0].Value != "synthetic-asset-" + asset)
                        throw new InvalidOperationException("Opaque lineage/payload/order disagreed with model");
                }
            }
        }
    }

    private sealed class Directory : IServerUserManager
    {
        public event EventHandler<ServerLoginEventArgs> OnLogin { add { } remove { } }
        public bool IsLoggedIn(string connectionId, string uuid) => true;
        public string[] GetConnections() => new[] { "connection-0", "connection-1" };
        public bool TryGetConnection(string uuid, out string connectionId) { connectionId = "connection-" + uuid; return true; }
        public bool TryGetDisplayName(string uuid, out string displayName) { displayName = uuid; return true; }
        public bool TryGetLoggedIn(string uuid, out bool loggedIn) { loggedIn = true; return true; }
        public bool TryGetAcceptingTrades(string uuid, out bool acceptingTrades) { acceptingTrades = true; return true; }
    }
}
