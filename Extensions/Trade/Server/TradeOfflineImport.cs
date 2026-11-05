using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Utils.Framework;

namespace Phinix.TradeExtension.Server;
// Format-neutral import boundary. Legacy parsing/field/default rules belong to the offline adapter.
public sealed class TradeImportRecord
{
    public FrameworkTradeStateSnapshot Snapshot {get;set;}
    public bool Terminal {get;set;}
    public bool Cancelled {get;set;}
}
public static class TradeOfflineImport
{
    public static void WriteCandidate(string path,IReadOnlyList<TradeImportRecord> records,string sourceFingerprint)
        =>PhinixFrameworkTradeStore.WriteImportCandidate(path,records,sourceFingerprint);
    public static IReadOnlyList<TradeImportRecord> ReadCandidate(string path)=>PhinixFrameworkTradeStore.ReadImportCandidate(path);
    public static void InitializeCandidate(string path)=>new PhinixFrameworkTradeStore().LoadDurable(path);
}
internal sealed partial class PhinixFrameworkTradeStore
{
    internal static IReadOnlyList<TradeImportRecord> ReadImportCandidate(string path)
    {
        if(!File.Exists(path+".sqlite3")||!File.Exists(path+".authority"))throw new InvalidDataException("Existing isolated candidate required.");
        var store=new PhinixFrameworkTradeStore();store.LoadDurable(path);
        var all=store.activeTrades.Values.Select(t=>new TradeImportRecord {Snapshot=t.ToSnapshot()}).ToList();
        foreach(var snapshot in store.terminalTrades)
        {var notifications=store.pendingCompletionNotifications.Where(n=>n.TradeId==snapshot.TradeId).ToArray();
            if(notifications.Length!=2||notifications.Any(n=>n.DeliveryState!="OutcomeUnknown")||notifications.Select(n=>n.Cancelled).Distinct().Count()!=1)throw new InvalidDataException("Historical delivery evidence differs.");
            all.Add(new TradeImportRecord {Snapshot=snapshot,Terminal=true,Cancelled=notifications[0].Cancelled});}
        return all;
    }
    internal static void WriteImportCandidate(string path,IReadOnlyList<TradeImportRecord> records,string fingerprint)
    {
        if(!TradeDeliveryProtocol.Hash(fingerprint)||records==null||File.Exists(path)||File.Exists(path+".sqlite3")||File.Exists(path+".authority"))throw new InvalidDataException("Use a new offline import candidate.");
        var state=new FrameworkTradeStoreState {ConsentSchemaVersion=1,OfferVersions=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase)};
        foreach(var record in records)
        {
            var snapshot=record.Snapshot??throw new InvalidDataException("Missing import trade.");
            if(record.Terminal)
            {
                state.TerminalTrades.Add(snapshot);
                foreach(var pending in PendingCompletionNotification.CreateMany(snapshot,record.Cancelled))
                {
                    var row=pending.ToState();row.DeliveryState="OutcomeUnknown";
                    row.DeliveryId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint+"\n"+snapshot.TradeId+"\n"+row.RecipientUuid))).Substring(0,32).ToLowerInvariant();
                    state.PendingCompletionNotifications.Add(row);
                }
            }
            else {state.ActiveTrades.Add(snapshot);state.OfferVersions.Add(snapshot.TradeId,snapshot.SnapshotVersion);}
        }
        byte[] bytes=Serialize(state);if(bytes.Length>MaxStateBytes)throw new InvalidDataException("Import candidate exceeds bounded hot ledger; no partial import.");
        new PhinixFrameworkTradeStore().ImportState(bytes);
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
    }
}
