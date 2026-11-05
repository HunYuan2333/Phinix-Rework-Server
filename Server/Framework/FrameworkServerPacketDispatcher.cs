using System;
using Connections;
using ServerRuntime;
using Utils.Framework;

namespace PhinixServer.Framework
{
    public sealed class FrameworkServerPacketDispatcher : IFrameworkServerPacketDispatcher, IResultServerPacketDispatcher
    {
        private Func<string, string, FrameworkPacket, PacketDispatchResult> dispatch;
        public void Configure(Action<string, string, FrameworkPacket> dispatch)
        {
            this.dispatch = dispatch == null ? null : (id, source, packet) =>
            {
                dispatch(id, source, packet);
                return PacketDispatchResult.Single(id, TransportSendOutcome.Unknown);
            };
        }
        public void ConfigureWithResult(Func<string, string, FrameworkPacket, PacketDispatchResult> dispatch) => this.dispatch = dispatch;
        public PacketDispatchResult SendWithResult(string connectionId, FrameworkPacket packet, string sourceExtensionId = null)
        {
            if (string.IsNullOrEmpty(connectionId) || packet == null || dispatch == null)
                return PacketDispatchResult.Single(connectionId, TransportSendOutcome.NotSent);
            try { return dispatch(connectionId, sourceExtensionId, packet) ?? PacketDispatchResult.Single(connectionId, TransportSendOutcome.Unknown); }
            catch { return PacketDispatchResult.Single(connectionId, TransportSendOutcome.Unknown); }
        }
        public void Send(string connectionId, FrameworkPacket packet) => Send(connectionId, packet, null);
        public void Send(string connectionId, FrameworkPacket packet, string sourceExtensionId) => SendWithResult(connectionId, packet, sourceExtensionId);
    }
}
