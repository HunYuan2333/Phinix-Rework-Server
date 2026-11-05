using System;
using System.Linq;
using System.Text;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using Utils.Framework;

namespace Phinix.TradeExtension.Server;

public static class TradeDeliveryProtocol
{
    public const string Request="trade.delivery.receipt.request", Response="trade.delivery.receipt.response";
    public const string Contract=DeliveryReceiptIntent.Contract, IdKey="trade.delivery.id", HashKey="trade.delivery.hash", ContractKey="trade.delivery.contract";
    internal static string Fingerprint(string id,string recipient,FrameworkTradeCompletionEvent body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("phinix.delivery.v1\n"+id+"\n"+recipient.ToUpperInvariant()+"\n"+FrameworkSerialization.SerializePayload(body))));
    internal static bool Hash(string value) => value?.Length==64 && value.All(Uri.IsHexDigit);
}
[DataContract] public sealed class TradeDeliveryReceiptRequest
{
    [DataMember(Order=0)] public string DeliveryId {get;set;}
    [DataMember(Order=1)] public string TradeId {get;set;}
    [DataMember(Order=2)] public bool Cancelled {get;set;}
    [DataMember(Order=3)] public string PayloadFingerprint {get;set;}
    [DataMember(Order=4)] public string Kind {get;set;}
    [DataMember(Order=5)] public string SaveIdentity {get;set;}
    [DataMember(Order=6)] public string CheckpointFingerprint {get;set;}
}
[DataContract] public sealed class TradeDeliveryReceiptResponse
{
    [DataMember(Order=0)] public string DeliveryId {get;set;}
    [DataMember(Order=1)] public bool Accepted {get;set;}
    [DataMember(Order=2)] public string State {get;set;}
    [DataMember(Order=3)] public string[] DeliveryStates {get;set;}
}
public interface ITradeDeliveryReceiptApi {void HandleDeliveryReceipt(FrameworkPacket command,ServerFrameworkContext context);}
public sealed partial class PhinixFrameworkTradeServerService : ITradeDeliveryReceiptApi
{
    public void HandleDeliveryReceipt(FrameworkPacket command,ServerFrameworkContext context)
    {
        if(context==null||readiness==null || !readiness.TryGetReady(context.ConnectionId,out var ready) || ready.PrincipalId!=context.SenderUuid || ready.SessionId!=context.SessionId ||
            !ready.Capabilities.Contains(TradeDeliveryProtocol.Request) || !ready.Capabilities.Contains(TradeDeliveryProtocol.Response))
            throw new UnauthorizedAccessException("Delivery receipt requires a negotiated ready session.");
        TradeDeliveryReceiptRequest request=null;
        if(command?.PayloadJson?.Length<=2048)try{request=FrameworkSerialization.DeserializePayload<TradeDeliveryReceiptRequest>(command.PayloadJson);}catch(SerializationException){}
        bool accepted=false;string state=null;
        if(request!=null && Guid.TryParseExact(request.DeliveryId,"N",out _) && !string.IsNullOrWhiteSpace(request.TradeId) && request.TradeId.Length<=128 &&
            TradeDeliveryProtocol.Hash(request.PayloadFingerprint) &&
            ((request.Kind=="InboxStored" && request.SaveIdentity==null && request.CheckpointFingerprint==null) ||
             (request.Kind=="SavedInventory" && TradeDeliveryProtocol.Hash(request.SaveIdentity) && TradeDeliveryProtocol.Hash(request.CheckpointFingerprint))))
        {
            // Stable receipt operation, independent of reconnect/session/message envelopes.
            accepted=store.Execute(context.SenderUuid,"receipt-"+request.DeliveryId+"-"+request.Kind,
                Intent("delivery-receipt",request),candidate=>new TradeOperationResult {Success=candidate.RecordDeliveryReceipt(context.SenderUuid,new DeliveryReceiptIntent(request.DeliveryId,request.TradeId,request.Cancelled,request.PayloadFingerprint,request.Kind,request.SaveIdentity,request.CheckpointFingerprint))},recovery:true).Success;
            if(accepted)state=store.ReceiptState(request.TradeId,context.SenderUuid);
        }
        var response=new FrameworkPacket {Flow=global::Phinix.Framework.FrameworkFlow.Command,CommandKind=global::Phinix.Framework.FrameworkCommandKind.Response,
            MessageType=TradeDeliveryProtocol.Response,SessionId=ready.SessionId,SenderUuid=FrameworkProtocol.SystemSenderUuid,
            PayloadJson=FrameworkSerialization.SerializePayload(new TradeDeliveryReceiptResponse {DeliveryId=request?.DeliveryId,Accepted=accepted,State=state,DeliveryStates=accepted?store.DeliveryStates(request.TradeId,context.SenderUuid):null})};
        response.SetCorrelationId(command?.GetCorrelationId());context.SendMessage?.Invoke(context.ConnectionId,response);
    }
}
