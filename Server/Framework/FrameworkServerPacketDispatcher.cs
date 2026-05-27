using System;
using Utils.Framework;

namespace PhinixServer.Framework
{
    public sealed class FrameworkServerPacketDispatcher : IFrameworkServerPacketDispatcher
    {
        private Action<string, string, FrameworkPacket> dispatch;

        public void Configure(Action<string, string, FrameworkPacket> dispatch)
        {
            this.dispatch = dispatch;
        }

        public void Send(string connectionId, FrameworkPacket packet)
        {
            Send(connectionId, packet, null);
        }

        public void Send(string connectionId, FrameworkPacket packet, string sourceExtensionId)
        {
            dispatch?.Invoke(connectionId, sourceExtensionId, packet);
        }
    }
}
