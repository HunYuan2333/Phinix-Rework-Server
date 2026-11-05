using System.Runtime.Serialization;
using Connections;
using Phinix.ChatExtension.Server;
using ServerRuntime;
using UserManagement;
using Utils.Framework;

namespace Phinix.LegacyAdapter.Server
{
    [DataContract]
    public sealed class LegacyChatServerConfig : IExtensionConfigSection
    {
        [DataMember(Order = 0)] public bool Enabled { get; set; }
        public string SectionName => "builtin.legacy-chat-adapter";
        public void LoadDefaults() => Enabled = true;
        public void Validate() { }
    }

    [PhinixExtension("builtin.legacy-chat-adapter", DependsOn = new[] { "builtin.chat" })]
    public sealed class LegacyChatServerExtension : IPhinixExtensionModule, IActivatablePhinixExtensionModule
    {
        public string ExtensionId => "builtin.legacy-chat-adapter";
        public int Priority => 900;
        private bool enabled;
        private OriginalChatAdapter adapter;
        public void Register(IExtensionBuilder builder)
        { enabled = builder.HostContext.GetRequiredService<IExtensionConfigProvider>().GetConfig<LegacyChatServerConfig>().Enabled; }
        public void Activate(ExtensionHostContext host)
        {
            if (!enabled) return;
            if (!host.TryResolveApi<IFrameworkChatServerApi>(out var chat)) throw new System.InvalidOperationException("Chat API unavailable");
            adapter = new OriginalChatAdapter(host.GetRequiredService<IServerTransport>(), host.GetRequiredService<IServerUserManager>(),
                host.GetRequiredService<IServerPrincipalAuthorization>(), host.GetRequiredService<IServerSessionReadiness>(), chat,
                message => host.GetRequiredService<IFrameworkServerBroadcaster>().Broadcast(ExtensionId, chat.BuildBroadcastPacket(message)), host.Log);
            try { adapter.Start(); } catch { adapter.Dispose(); adapter = null; throw; }
        }
        public void Shutdown(ExtensionHostContext host) { var old = adapter; adapter = null; old?.Dispose(); }
    }
}
