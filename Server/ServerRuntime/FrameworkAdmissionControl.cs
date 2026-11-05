using System.Runtime.Serialization;

namespace ServerRuntime
{
    // Control evidence about this admission attempt, never a business operation result/ACK.
    [DataContract]
    public sealed class FrameworkAdmissionRejection
    {
        public const string PacketKind = "control";
        public const string PacketType = "framework.admission.rejected";
        [DataMember(Order = 0)] public string Code { get; set; }
        [DataMember(Order = 1)] public string RequestMessageId { get; set; }
        [DataMember(Order = 2)] public string CorrelationId { get; set; }
        [DataMember(Order = 3)] public string Stage { get; set; }
        [DataMember(Order = 4)] public string OperationOutcome { get; set; } = "Unknown";
        [DataMember(Order = 5)] public bool Retryable { get; set; }
        [DataMember(Order = 6)] public bool RequestInvoked { get; set; }
    }
}
