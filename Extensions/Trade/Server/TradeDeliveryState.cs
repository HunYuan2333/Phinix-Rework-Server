using System;
using System.Linq;
namespace Phinix.TradeExtension.Server;

// Immutable business intent; no wire envelope, session, profile or compatibility policy.
internal sealed record DeliveryReceiptIntent(string DeliveryId,string TradeId,bool Cancelled,
    string PayloadFingerprint,string Kind,string SaveIdentity,string CheckpointFingerprint)
{
    internal const string Contract="DurableInboxV1";
}
internal sealed partial class PhinixFrameworkTradeStore
{
    internal bool RecordDeliveryReceipt(string recipient,DeliveryReceiptIntent receipt)
    {
        var notification=pendingCompletionNotifications.SingleOrDefault(n=>n.DeliveryId==receipt.DeliveryId && n.RecipientUuid==recipient);
        if(notification==null || notification.TradeId!=receipt.TradeId || notification.Cancelled!=receipt.Cancelled || notification.ReceiptContract!=DeliveryReceiptIntent.Contract ||
            notification.DeliveryState=="Pending" || !string.Equals(notification.PayloadFingerprint(),receipt.PayloadFingerprint,StringComparison.OrdinalIgnoreCase))return false;
        if(notification.ReceiptKind=="SavedInventory")return receipt.Kind=="InboxStored" ||
            (notification.ReceiptSave==receipt.SaveIdentity && notification.ReceiptCheckpoint==receipt.CheckpointFingerprint);
        notification.ReceiptKind=receipt.Kind;notification.ReceiptSave=receipt.SaveIdentity;notification.ReceiptCheckpoint=receipt.CheckpointFingerprint;
        if(receipt.Kind=="SavedInventory" || notification.Items.Count==0)notification.DeliveryState="Confirmed";
        return true;
    }
}
