using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Phinix.TradeExtension.Server;
using Phinix.TradeExtension;
using Trading;
using Utils.Framework;

if(args.Length==2&&args[0]=="--verify-restore")
{
    string dir=Path.GetFullPath(args[1]);var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"import.json"))).RootElement;
    string source=Path.Combine(dir,"source.original");
    if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))!=report.GetProperty("sourceSha256").GetString())throw new InvalidDataException("Original restore source changed.");
    Console.WriteLine("Original restore source hash verified. Restoring it after new writes requires an explicit consistent migration; no live state was changed.");return;
}
if(args.Length!=3||(args[0]!="protobuf"&&args[0]!="xml"))throw new ArgumentException("Usage: LegacyTradeImportAdapter protobuf|xml SOURCE NEW_OUTPUT_DIRECTORY; or --verify-restore DIRECTORY. Stop all writers first.");
string input=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
if(!File.Exists(input)||new FileInfo(input).Length>4*1024*1024||Directory.Exists(output)||File.Exists(output))throw new InvalidDataException("Bounded source and a new output directory required.");
Directory.CreateDirectory(output);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(output,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
byte[] original=File.ReadAllBytes(input);string sourceHash=Convert.ToHexString(SHA256.HashData(original));
string backup=Path.Combine(output,"source.original");File.WriteAllBytes(backup,original);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(backup,UnixFileMode.UserRead|UnixFileMode.UserWrite);
try
{
    IReadOnlyList<TradeImportRecord> expected=null;
    string candidate=Path.Combine(output,"trades");var payloads=new List<object>();int activeCount=0,terminalCount=0,resetConsent=0;
    if(args[0]=="xml")File.Copy(backup,candidate,false);
    else
    {
        var store=ActiveTradesStore.Parser.ParseFrom(original);
        // Original generated readers discard unknown tags. Require a lossless round trip of the complete source before translating.
        if(!store.ToByteArray().SequenceEqual(original))throw new InvalidDataException("Unknown, duplicated or noncanonical historical protobuf fields; whole source quarantined.");var records=new List<TradeImportRecord>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TradeImportRecord Translate(TradeStore trade,bool terminal,bool cancelled)
        {
            if(string.IsNullOrWhiteSpace(trade.TradeId)||!seen.Add(trade.TradeId)||trade.PartyUuids.Count!=2||trade.PartyUuids.Any(string.IsNullOrWhiteSpace)||trade.PartyUuids.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2 ||
                trade.ItemsOnOffer.Keys.Any(k=>!trade.PartyUuids.Contains(k))||trade.AcceptedParties.Any(k=>!trade.PartyUuids.Contains(k)))throw new InvalidDataException("Invalid legacy trade/owner references; no partial import.");
            resetConsent+=trade.AcceptedParties.Count;
            var snapshot=new FrameworkTradeStateSnapshot {TradeId=trade.TradeId,SnapshotVersion=1,Participants=trade.PartyUuids.Select(owner=>
            {
                var items=new List<FrameworkItemPayload>();if(trade.ItemsOnOffer.TryGetValue(owner,out var things))foreach(var thing in things.Things)
                {
                    if(string.IsNullOrWhiteSpace(thing.DefName)||thing.StackCount<=0)throw new InvalidDataException("Invalid legacy item; whole source quarantined.");
                    byte[] bytes=thing.ToByteArray();payloads.Add(new {trade=trade.TradeId,owner,sha256=Convert.ToHexString(SHA256.HashData(bytes)),bytes=bytes.Length});
                    var translated=LegacyItemTranslation.Translate(thing);
                    items.Add(translated);payloads.Add(new {targetPayloadSha256=Convert.ToHexString(SHA256.HashData(translated.PayloadBytes)),targetCodec=translated.CodecId});
                }
                return new FrameworkTradeParticipantSnapshot {Uuid=owner,Accepted=false,ItemsOnOffer=items};
            }).ToList()};
            return new TradeImportRecord {Snapshot=snapshot,Terminal=terminal,Cancelled=cancelled};
        }
        foreach(var trade in store.ActiveTrades){records.Add(Translate(trade,false,false));activeCount++;}
        foreach(var completed in store.CompletedTrades)
        {
            if(completed.Trade==null||completed.PendingNotification.Any(p=>!completed.Trade.PartyUuids.Contains(p))||completed.PendingNotification.Distinct().Count()!=completed.PendingNotification.Count)throw new InvalidDataException("Invalid legacy pending recipients.");
            records.Add(Translate(completed.Trade,true,completed.Cancelled));terminalCount++;
        }
        expected=records;TradeOfflineImport.WriteCandidate(candidate,records,sourceHash);
    }
    TradeOfflineImport.InitializeCandidate(candidate);
    var actual=TradeOfflineImport.ReadCandidate(candidate);
    string Canonical(IEnumerable<TradeImportRecord> records)=>JsonSerializer.Serialize(records.OrderBy(r=>r.Snapshot.TradeId,StringComparer.Ordinal).Select(r=>new {r.Terminal,r.Cancelled,
        snapshot=FrameworkSerialization.SerializePayload(r.Snapshot)}));
    if(expected!=null && Canonical(expected)!=Canonical(actual))throw new InvalidDataException("Committed candidate differs from translated source; no cutover.");
    activeCount=actual.Count(r=>!r.Terminal);terminalCount=actual.Count(r=>r.Terminal);
    File.WriteAllText(Path.Combine(output,"import.json"),JsonSerializer.Serialize(new {sourceFormat=args[0],sourceSha256=sourceHash,activeCount,terminalCount,unboundConsentReset=resetConsent,payloads,
        pendingRule="All historical terminal deliveries remain OutcomeUnknown; historical send/absence is not durable ACK.",codec="Lossless known basic fields translate to core.item.vanilla; unknown fields/tunnels quarantine the entire source",authority="trades.sqlite3; original input and XML evidence are retained; no long-term dual writes"},new JsonSerializerOptions {WriteIndented=true}));
    Console.WriteLine("Validated isolated candidate created. Inspect import.json and run --verify-restore before any separately authorized cutover.");
}
catch(Exception error)
{
    string quarantine=Path.Combine(output,"quarantine");Directory.CreateDirectory(quarantine);File.Copy(backup,Path.Combine(quarantine,"source.original"));File.WriteAllText(Path.Combine(quarantine,"reason.txt"),error.GetType().Name);
    throw;
}
