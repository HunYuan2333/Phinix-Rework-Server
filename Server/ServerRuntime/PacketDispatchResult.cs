using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Connections;
using Utils.Framework;

namespace ServerRuntime
{
    // Neutral, server-only optional service. No Common ABI/profile/business dependencies.
    public interface IResultServerPacketDispatcher
    {
        PacketDispatchResult SendWithResult(string connectionId, FrameworkPacket packet, string sourceExtensionId = null);
    }

    public sealed class PacketDispatchResult
    {
        public IReadOnlyDictionary<string, TransportSendOutcome> Outcomes { get; }
        public bool HasUncertainFailure { get; }
        public bool HasPossibleDelivery => HasUncertainFailure || Outcomes.Values.Any(value => value != TransportSendOutcome.NotSent);
        public PacketDispatchResult(IDictionary<string, TransportSendOutcome> outcomes, bool uncertainFailure = false)
        {
            HasUncertainFailure = uncertainFailure;
            Outcomes = new ReadOnlyDictionary<string, TransportSendOutcome>(new Dictionary<string, TransportSendOutcome>(outcomes));
        }
        public TransportSendOutcome For(string connectionId) => connectionId != null && Outcomes.TryGetValue(connectionId, out var value) ? value : HasUncertainFailure ? TransportSendOutcome.Unknown : TransportSendOutcome.NotSent;
        public static PacketDispatchResult Single(string connectionId, TransportSendOutcome outcome) =>
            new PacketDispatchResult(string.IsNullOrEmpty(connectionId) ? new Dictionary<string, TransportSendOutcome>() :
                new Dictionary<string, TransportSendOutcome> { [connectionId] = outcome });
    }
}
