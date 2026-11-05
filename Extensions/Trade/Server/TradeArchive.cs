using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using ServerStorage;
using Utils.Framework;

namespace Phinix.TradeExtension.Server;
[DataContract] internal sealed class ArchivedTrade
{
    [DataMember(Order=0)] public FrameworkTradeStateSnapshot Snapshot {get;set;}
    [DataMember(Order=1)] public List<PhinixFrameworkTradeStore.PendingCompletionNotificationState> Deliveries {get;set;}
    [DataMember(Order=2, EmitDefaultValue=false)] public TradeNegotiationTerms Terms {get;set;}
}
internal sealed partial class PhinixFrameworkTradeStore
{
    private static string ArchiveKey(string trade)=>"trade.archive."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trade.ToUpperInvariant())));
    private void ArchiveConfirmed(JournalTransaction tx)
    {
        // One bounded record per transaction. Unknown/best-effort rows are never evicted.
        var completed=terminalTrades.FirstOrDefault(t=>
        {var rows=pendingCompletionNotifications.Where(n=>n.TradeId==t.TradeId).ToArray();return rows.Length==2&&rows.All(n=>n.DeliveryState=="Confirmed");});
        if(completed==null)return;
        string key=ArchiveKey(completed.TradeId);if(tx.ReadState(key)!=null)throw new System.IO.InvalidDataException("Conflicting archived trade.");
        tx.WriteState(key,Serialize(new ArchivedTrade {Snapshot=completed,Terms=GetTerms(completed.TradeId),Deliveries=pendingCompletionNotifications.Where(n=>n.TradeId==completed.TradeId).Select(n=>n.ToState()).ToList()}));
        terminalTrades.Remove(completed);negotiationTerms.Remove(completed.TradeId);pendingCompletionNotifications.RemoveAll(n=>n.TradeId==completed.TradeId);
    }
    internal string ReceiptState(string trade,string principal)
    {
        var states=DeliveryStates(trade,principal);return states?.SingleOrDefault(s=>s.StartsWith(principal+":",StringComparison.Ordinal))?.Substring(principal.Length+1);
    }
    internal string[] DeliveryStates(string trade,string principal)
    {
        lock(syncLock)
        {
            EnsurePersistence();var snapshot=activeTrades.TryGetValue(trade,out var active)?active.ToSnapshot():terminalTrades.SingleOrDefault(t=>t.TradeId==trade);
            var rows=pendingCompletionNotifications.Where(n=>n.TradeId==trade).ToArray();
            if(snapshot==null && journal!=null)
            {
                var raw=journal.ReadState(ArchiveKey(trade));if(raw==null)return null;var archived=Deserialize<ArchivedTrade>(raw);
                var check=new PhinixFrameworkTradeStore();
                if(!Enum.IsDefined(archived.Terms))throw new System.IO.InvalidDataException("Invalid archived trade terms.");
                check.ImportState(Serialize(new FrameworkTradeStoreState {ActiveTrades=new(),TerminalTrades=new(){archived.Snapshot},PendingCompletionNotifications=archived.Deliveries,ConsentSchemaVersion=1,OfferVersions=new(),NegotiationTerms=archived.Terms==TradeNegotiationTerms.ObservedQuote?null:new(){{trade,archived.Terms}}}));
                if(archived.Snapshot.TradeId!=trade || archived.Deliveries.Count!=2 || archived.Deliveries.Any(d=>d.DeliveryState!="Confirmed"))throw new System.IO.InvalidDataException("Invalid archived trade.");
                snapshot=archived.Snapshot;rows=archived.Deliveries.Select(PendingCompletionNotification.FromState).ToArray();
            }
            if(snapshot==null||!snapshot.Participants.Any(p=>p.Uuid==principal))return null;
            return snapshot.Participants.Select(p=>p.Uuid+":"+(rows.SingleOrDefault(n=>n.RecipientUuid==p.Uuid)?.DeliveryState??"Negotiating")).ToArray();
        }
    }
}
