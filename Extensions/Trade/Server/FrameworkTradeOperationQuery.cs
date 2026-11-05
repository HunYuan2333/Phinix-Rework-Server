using System;
using System.Linq;
using System.Runtime.Serialization;
using ServerStorage;
using Utils.Framework;

namespace Phinix.TradeExtension.Server;

// Additive server wire contract. Existing shared contracts and historical profiles stay pinned.
public static class FrameworkTradeOperationQueryProtocol
{
    public const string RequestType = "trade.operation.query.request";
    public const string ResponseType = "trade.operation.query.response";
}

[DataContract]
public sealed class FrameworkTradeOperationQueryRequest
{
    [DataMember(Order = 0)] public string OperationId { get; set; }
    [DataMember(Order = 1)] public string RequestFingerprint { get; set; }
}

[DataContract]
public sealed class FrameworkTradeOperationQueryResponse
{
    [DataMember(Order = 0)] public string OperationId { get; set; }
    [DataMember(Order = 1)] public string Status { get; set; }
    [DataMember(Order = 2)] public bool? Success { get; set; }
    [DataMember(Order = 3)] public FrameworkTradeFailureReason? Failure { get; set; }
    [DataMember(Order = 4)] public string TradeId { get; set; }
    [DataMember(Order = 5)] public long? SnapshotVersion { get; set; }
    [DataMember(Order = 6)] public bool Completed { get; set; }
    [DataMember(Order = 7)] public bool Cancelled { get; set; }
}

public interface IFrameworkTradeOperationQueryApi
{
    void HandleOperationQueryRequest(FrameworkPacket command, ServerFrameworkContext context);
}

public sealed partial class PhinixFrameworkTradeServerService : IFrameworkTradeOperationQueryApi
{
    public void HandleOperationQueryRequest(FrameworkPacket command, ServerFrameworkContext context)
    {
        // Defense in depth: no unhosted/in-memory query and no client-supplied principal.
        if (context == null || readiness == null ||
            !readiness.TryGetReady(context.ConnectionId, out var ready) ||
            ready.PrincipalId != context.SenderUuid || ready.SessionId != context.SessionId ||
            !ready.Capabilities.Contains(FrameworkTradeOperationQueryProtocol.RequestType, StringComparer.OrdinalIgnoreCase) ||
            !ready.Capabilities.Contains(FrameworkTradeOperationQueryProtocol.ResponseType, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Operation query requires a negotiated ready session.");

        FrameworkTradeOperationQueryRequest request = null;
        if (command?.PayloadJson?.Length <= 1024)
        {
            try { request = FrameworkSerialization.DeserializePayload<FrameworkTradeOperationQueryRequest>(command.PayloadJson); }
            catch (SerializationException) { /* Malformed input has no business effect. */ }
        }
        var response = new FrameworkTradeOperationQueryResponse { Status = "InvalidRequest" };
        if (request != null && ValidQueryId(request.OperationId) && request.RequestFingerprint?.Length == 64 &&
            TryFingerprint(request.RequestFingerprint, out var fingerprint))
        {
            response.OperationId = request.OperationId;
            try
            {
                var result = store.QueryOperation(context.SenderUuid, request.OperationId, fingerprint);
                response.Status = result == null ? "NotFound" : "Committed";
                if (result != null)
                {
                    response.Success = result.Success; response.Failure = result.Failure;
                    response.TradeId = result.Snapshot?.TradeId; response.SnapshotVersion = result.Snapshot?.SnapshotVersion;
                    response.Completed = result.Completed; response.Cancelled = result.Cancelled;
                }
            }
            catch (OperationConflictException) { response.Status = "Conflict"; }
        }
        var packet = new FrameworkPacket
        {
            Flow = global::Phinix.Framework.FrameworkFlow.Command,
            CommandKind = global::Phinix.Framework.FrameworkCommandKind.Response,
            MessageType = FrameworkTradeOperationQueryProtocol.ResponseType,
            SessionId = ready.SessionId, SenderUuid = FrameworkProtocol.SystemSenderUuid,
            PayloadJson = FrameworkSerialization.SerializePayload(response)
        };
        packet.SetCorrelationId(command?.GetCorrelationId());
        context.SendMessage?.Invoke(context.ConnectionId, packet);
    }

    private static bool ValidQueryId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Contains('\0');
    private static bool TryFingerprint(string text, out byte[] bytes)
    {
        try { bytes = Convert.FromHexString(text); return bytes.Length == 32; }
        catch (FormatException) { bytes = null; return false; }
    }
}
