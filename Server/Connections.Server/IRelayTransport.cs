namespace Connections
{
    // Optional bounded handoff for opaque client traffic. Existing business sends retain their contract.
    public interface IRelayTransport
    {
        TransportSendOutcome TrySendRelay(string connectionId, string module, byte[] payload);
    }
}
