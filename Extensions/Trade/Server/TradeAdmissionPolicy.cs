namespace Phinix.TradeExtension.Server;

// Immutable domain policy, independent of config deserialization or protocol profiles.
internal sealed class TradeAdmissionPolicy(int retainedTrades = 32, int payloadBytes = 1024 * 1024, int auxiliaryBytes = 64 * 1024,long storageBytes=256L*1024*1024)
{
    public long StorageBytes {get;}=storageBytes;
    public int RetainedTrades { get; } = retainedTrades;
    public int PayloadBytes { get; } = payloadBytes;
    public int AuxiliaryBytes { get; } = auxiliaryBytes;
}
