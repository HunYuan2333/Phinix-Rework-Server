namespace Connections
{
    // Transport evidence only. None of these values confirms remote receipt, application,
    // game save, or domain COMMIT. Unknown/Submitted never authorize refund or blind replay.
    public enum TransportSendOutcome
    {
        Unknown = 0,
        NotSent = 1,
        // Transport owns bytes: UDP handoff or bounded TLS queue admission. No flush/remote ACK implied.
        Submitted = 2
    }
}
