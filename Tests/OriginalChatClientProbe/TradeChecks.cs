using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Trading;

internal static partial class Program
{
    private sealed partial class Peer
    {
        public ClientTrading Trade;
        public string CreateFailure;
        public string OfferFailureToken;
        private readonly List<CompleteTradeEventArgs> completions = new List<CompleteTradeEventArgs>();
        private void InitializeTrade()
        {
            Trade = new ClientTrading(Net, Auth, Users);
            Trade.OnTradeCreationFailure += (_, e) => { CreateFailure = e.FailureReason + ": " + e.FailureMessage; changed.Set(); };
            Trade.OnTradeCompleted += (_, e) => { lock (gate) completions.Add(e); changed.Set(); };
            Trade.OnTradeCancelled += (_, e) => { lock (gate) completions.Add(e); changed.Set(); };
            Trade.OnTradeUpdateFailure += (_, e) => { OfferFailureToken = e.Token; changed.Set(); };
            Trade.OnTradeUpdateSuccess += (_, __) => changed.Set();
        }
        public CompleteTradeEventArgs Completion(string id)
        { lock (gate) return completions.SingleOrDefault(c => c.TradeId == id); }
        public int CompletionCount { get { lock (gate) return completions.Count; } }
        public bool Offered(string id, string owner, string def, int count)
        {
            if (!Trade.TryGetItemsOnOffer(id, owner, out var items)) return false;
            var array = items.ToArray();
            return count == 0 ? array.Length == 0 : array.Length == 1 && array[0].DefName == def && array[0].StackCount == count;
        }
    }
    private static string Create(Peer a, Peer b)
    {
        a.Trade.CreateTrade(b.Users.Uuid);
        a.Await(() => a.Trade.GetTradeIds().Length == 1 || a.CreateFailure != null, "Actual original create failed without response");
        Check(a.CreateFailure == null, "Actual original create failed: " + a.CreateFailure);
        string id = a.Trade.GetTradeIds().Single();
        b.Await(() => b.Trade.GetTradeIds().Contains(id), "Other original could not see new trade");
        return id;
    }
    private static void Finished(Peer peer, string id, bool success, string def, int count)
    {
        peer.Await(() => peer.Completion(id) != null && !peer.Trade.GetTradeIds().Contains(id), "Original completion/cancellation absent");
        var result = peer.Completion(id); var items = result.Items.ToArray();
        Check(result.Success == success && (count == 0 ? items.Length == 0 : items.Length == 1 && items[0].DefName == def && items[0].StackCount == count), "Original completion changed success, recipient assets or counts");
    }
    private static ProtoThing Item(string def, int count) => new ProtoThing { DefName = def, StackCount = count, Quality = Quality.None, HitPoints = 60 };
    private static void TradeChecks(Peer a, Peer b, string root, string phase)
    {
        if (phase == "trade-mixed")
        {
            a.Await(() => File.Exists(Path.Combine(root, "trade-modern-ready")), "Modern trade Ready barrier");
            string modern = File.ReadAllText(Path.Combine(root, "trade-modern-ready"));
            File.WriteAllText(Path.Combine(root, "trade-original-ready"), a.Users.Uuid);
            a.Trade.CreateTrade(modern);
            a.Await(() => a.Trade.GetTradeIds().Length == 1, "Original could not initiate toward Framework");
            string id = a.Trade.GetTradeIds().Single();
            a.Trade.UpdateItems(id, new[] { Item("Steel", 13) }, "mixed-native-offer");
            a.Await(() => a.Trade.TryGetItemsOnOffer(id, modern, out var items) && items.Select(i => i.StackCount).SequenceEqual(new[] { 500, 300 }) && items.All(i => i.DefName == "Silver" && i.HitPoints == -1), "800 silver did not reach actual original decoder with both stacks and sentinel");
            a.Trade.UpdateStatus(id, accepted: true);
            a.Await(() => a.Completion(id) != null && !a.Trade.GetTradeIds().Contains(id), "Original silver completion absent");
            var silver = a.Completion(id);
            Check(silver.Success && silver.Items.Select(i => i.StackCount).SequenceEqual(new[] { 500, 300 }) && silver.Items.All(i => i.DefName == "Silver" && i.HitPoints == -1), "Original completion did not preserve all 800 silver");
            File.WriteAllText(Path.Combine(root, "trade-mixed-first-done"), "done");
            a.Await(() => a.Trade.GetTradeIds().Length == 1, "Modern could not initiate toward original");
            id = a.Trade.GetTradeIds().Single(); a.Trade.CancelTrade(id); Finished(a, id, false, "", 0);
            a.Await(() => File.Exists(Path.Combine(root, "trade-modern-done")), "Modern strict-pair verification barrier");
        }
        else if (phase == "trade-initial")
        {
            string id = Create(a, b);
            // B has never submitted any Trading packet: cancellation must still finish,
            // even without a quote or acceptance from the other party.
            a.Trade.CancelTrade(id); Finished(a, id, false, "", 0); Finished(b, id, false, "", 0);

            id = Create(a, b);
            a.Trade.UpdateItems(id, new[] { Item("Steel", 7) }, "native-offer-a");
            b.Await(() => b.Offered(id, a.Users.Uuid, "Steel", 7), "Original quote not delivered to other party");
            a.Trade.UpdateItems(id, new[] { Item("Steel", 999) }, "native-offer-a");
            a.Await(() => a.OfferFailureToken == "native-offer-a", "Conflicting original token was not explicitly refused");
            Check(a.Offered(id, a.Users.Uuid, "Steel", 7) && b.Offered(id, a.Users.Uuid, "Steel", 7), "Conflicting token changed original quote");
            b.Trade.UpdateItems(id, new[] { Item("WoodLog", 3) }, "native-offer-b");
            a.Await(() => a.Offered(id, b.Users.Uuid, "WoodLog", 3), "Reverse original quote missing");
            a.Trade.UpdateStatus(id, accepted: true);
            b.Await(() => b.Trade.TryGetOtherPartyAccepted(id, out bool accepted) && accepted, "Original unversioned consent not propagated");
            b.Trade.UpdateStatus(id, accepted: true);
            Finished(a, id, true, "WoodLog", 3); Finished(b, id, true, "Steel", 7);

            id = Create(a, b);
            a.Trade.UpdateItems(id, new[] { Item("Steel", 9) }, "native-before-reset");
            b.Await(() => b.Offered(id, a.Users.Uuid, "Steel", 9), "Reset setup quote missing");
            a.Trade.UpdateItems(id, new ProtoThing[0]); // Exact original tokenless Reset.
            b.Await(() => b.Offered(id, a.Users.Uuid, "", 0), "Original tokenless reset failed");
            a.Trade.CancelTrade(id); Finished(a, id, false, "", 0); Finished(b, id, false, "", 0);

            id = Create(a, b);
            a.Trade.UpdateItems(id, new[] { Item("Steel", 11) }, "native-offline-a");
            b.Await(() => b.Offered(id, a.Users.Uuid, "Steel", 11), "Offline setup A quote missing");
            b.Trade.UpdateItems(id, new[] { Item("WoodLog", 5) }, "native-offline-b");
            a.Await(() => a.Offered(id, b.Users.Uuid, "WoodLog", 5), "Offline setup B quote missing");
            string offlineUuid = b.Users.Uuid;
            b.Net.Disconnect();
            a.Await(() => a.Users.GetUsers().Any(u => u.Uuid == offlineUuid && !u.LoggedIn), "Offline presence barrier", seconds: 40);
            a.Trade.CancelTrade(id); Finished(a, id, false, "Steel", 11);
            File.WriteAllText(Path.Combine(root, "trade-expected.json"), new JavaScriptSerializer().Serialize(new Dictionary<string, string> { ["cancelled"] = id, ["a"] = a.Users.Uuid, ["b"] = offlineUuid }));
        }
        else
        {
            var expected = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "trade-expected.json")));
            Check(a.Users.Uuid == expected["a"] && b.Users.Uuid == expected["b"], "Restart changed original identities");
            Check(b.CompletionCount == 0, "Unidentified login consumed asset-bearing Pending delivery");
            string id = Create(b, a); // Genuine raw trade traffic identifies the receiver.
            Finished(b, expected["cancelled"], false, "WoodLog", 5);
            a.Trade.CancelTrade(id); Finished(a, id, false, "", 0); Finished(b, id, false, "", 0);
            int count = b.CompletionCount;
            id = Create(b, a); a.Trade.CancelTrade(id); Finished(b, id, false, "", 0);
            Check(b.CompletionCount == count + 1, "Unknown original delivery was replayed as new assets");
        }
    }
}
