using Utils.Framework;

namespace PhinixServer.Framework
{
    public sealed class FrameworkServerPacketDispatcher : IFrameworkServerPacketDispatcher
    {
        private readonly NetServerAdapter sender;

        public FrameworkServerPacketDispatcher(NetServerAdapter sender)
        {
            this.sender = sender;
        }

        public void Send(string connectionId, FrameworkPacket packet)
        {
            sender?.Send(connectionId, packet);
        }

        public sealed class NetServerAdapter
        {
            private readonly Connections.NetServer server;

            public NetServerAdapter(Connections.NetServer server)
            {
                this.server = server;
            }

            public void Send(string connectionId, FrameworkPacket packet)
            {
                if (server == null || packet == null)
                {
                    return;
                }

                server.TrySend(connectionId, FrameworkProtocol.ModuleName, FrameworkSerialization.SerializePacket(packet));
            }
        }
    }
}
