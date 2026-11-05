using System;
using Connections;
using Utils;
using System.Runtime.Serialization;
using Phinix.TradeExtension.Server;
using ServerRuntime;
using UserManagement;
using Utils.Framework;

namespace Phinix.LegacyAdapter.Server
{
    [DataContract]
    public sealed class OriginalTradeServerConfig : IExtensionConfigSection
    {
        [DataMember(Order = 0)] public bool Enabled { get; set; }
        public string SectionName => "builtin.original-trade-adapter";
        public void LoadDefaults() => Enabled = false;
        public void Validate() { }
    }

    // Preserve the previous module identity for extension-policy compatibility.
    // Enabled profile implements the explicitly accepted weaker original trade terms.
    [PhinixExtension("builtin.original-trade-refusal", DependsOn = new[] { "builtin.trade" })]
    public sealed class OriginalTradeRefusalAdapter : IPhinixExtensionModule, IActivatablePhinixExtensionModule
    {
        public string ExtensionId => "builtin.original-trade-refusal";
        public int Priority => 901;
        private IServerTransport transport;
        private bool enabled;
        private OriginalTradeAdapter adapter;
        private Action<string, LogLevel> log;
        public void Register(IExtensionBuilder builder)
        { enabled = builder.HostContext.GetRequiredService<IExtensionConfigProvider>().GetConfig<OriginalTradeServerConfig>().Enabled; }
        public void Activate(ExtensionHostContext host)
        {
            if (enabled)
            {
                if (adapter != null) throw new InvalidOperationException("OriginalTradeAlreadyActive");
                if (!host.TryResolveApi<IFrameworkTradeServerApi>(out var trade)) throw new InvalidOperationException("Trade API unavailable");
                var authorization = host.GetRequiredService<IServerPrincipalAuthorization>();
                var sessions = authorization as IServerAuthenticatedSessionLookup ?? throw new InvalidOperationException("Authenticated session lookup unavailable");
                adapter = new OriginalTradeAdapter(host.GetRequiredService<IServerTransport>(), host.GetRequiredService<IServerUserManager>(), authorization,
                    sessions, host.GetRequiredService<IServerSessionReadiness>(), trade,
                    (connection, packet) => host.GetRequiredService<IFrameworkServerPacketDispatcher>().Send(connection, packet, "builtin.trade"), host.Log);
                try { adapter.Start(); } catch { adapter.Dispose(); adapter = null; throw; }
                return;
            }
            if (transport != null) throw new InvalidOperationException("OriginalTradeRefusalAlreadyActive");
            var candidate = host.GetRequiredService<IServerTransport>();
            log = host.Log;
            candidate.RegisterPacketHandler("Trading", Refuse);
            transport = candidate;
        }
        private void Refuse(string module, string connection, byte[] bytes)
        {
            // No packet parsing, asset mutation, receipt, or credential logging.
            try { log?.Invoke("OriginalTradingDisabled: enable builtin.original-trade-adapter only when the accepted basic-item/current-quote profile is intended. No trade action invoked.", LogLevel.WARNING); }
            catch { }
            transport?.DisconnectPeer(connection);
        }
        public void Shutdown(ExtensionHostContext host)
        {
            var oldAdapter = adapter; adapter = null; oldAdapter?.Dispose();
            var old = transport;
            old?.UnregisterPacketHandler("Trading");
            transport = null; log = null;
        }
    }
}
