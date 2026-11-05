using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Authentication;
using Connections;
using PhinixServer.Framework;
using Utils.Framework;
using UserManagement;

internal static class FrameworkLifecycleAuditTests
{
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    private sealed class Policy : IExtensionDiscoveryPolicy
    {
        public bool ShouldScanAssembly(Assembly a) => a == typeof(AuditPlugin).Assembly;
        public bool ShouldDiscoverType(Type t) => t == typeof(AuditPlugin);
    }
    [PhinixExtension("audit.neutral-lifecycle")]
    public sealed class AuditPlugin : IPhinixExtensionModule, IActivatablePhinixExtensionModule
    {
        public static bool Enabled, Activated, StartupRefused;
        public static int Stops;
        public string ExtensionId => "audit.neutral-lifecycle";
        public int Priority => 0;
        public void Register(IExtensionBuilder b) { }
        public void Activate(ExtensionHostContext h)
        {
            if (!Enabled) return;
            var broadcaster = h.GetRequiredService<IFrameworkServerBroadcaster>();
            try { broadcaster.Broadcast(ExtensionId, new FrameworkPacket { MessageType = "synthetic.startup" }); }
            catch (InvalidOperationException) { StartupRefused = true; }
            Activated = true;
        }
        public void Shutdown(ExtensionHostContext h) { if (Enabled) Stops++; }
    }
    private static ExtensionHostContext Context()
    { var c = new ExtensionHostContext(); c.AddService<IExtensionDiscoveryPolicy>(new Policy()); return c; }
    public static void Run()
    {
        using var net = new NetServer(new IPEndPoint(IPAddress.Any,0),2);
        var auth = new ServerAuthenticator(net,"synthetic","synthetic",AuthTypes.ClientKey);
        var users = new ServerUserManager(net,auth,100);
        var field = typeof(NetCommon).GetField("registeredPacketHandlers",BindingFlags.NonPublic|BindingFlags.Instance);
        var handlers = (Dictionary<string,NetCommon.PacketHandlerDelegate>)field.GetValue(net);
        int baseline = handlers.Count;
        var closedField = typeof(NetServer).GetField("OnConnectionClosed",BindingFlags.NonPublic|BindingFlags.Instance);
        int Subscriptions() => ((Delegate)closedField.GetValue(net))?.GetInvocationList().Length ?? 0;
        int originalSubscriptions = Subscriptions();
        AuditPlugin.Enabled = true; AuditPlugin.Activated = AuditPlugin.StartupRefused = false; AuditPlugin.Stops = 0;
        try
        {
            var framework = new PhinixFrameworkServer(net,auth,users,Context());
            try { Check(AuditPlugin.Activated && AuditPlugin.StartupRefused, "Neutral broadcaster unavailable in activation or startup send was not explicitly refused"); }
            finally { framework.ShutdownExtensions(); }
            framework.ShutdownExtensions();
            Check(handlers.Count == baseline && Subscriptions() == originalSubscriptions && AuditPlugin.Stops == 1,
                "Shutdown leaked owned raw module/subscription or cleaned a module twice");
            var next = new PhinixFrameworkServer(net,auth,users,Context()); next.ShutdownExtensions();
            NetCommon.PacketHandlerDelegate owned = (_,_,_) => { };
            net.RegisterPacketHandler(FrameworkProtocol.ModuleName,owned);
            int stops = AuditPlugin.Stops; bool refused = false;
            try { _ = new PhinixFrameworkServer(net,auth,users,Context()); }
            catch (PacketHandlerAlreadyRegisteredException) { refused = true; }
            Check(refused && AuditPlugin.Stops == stops+1 && handlers[FrameworkProtocol.ModuleName] == owned && Subscriptions() == originalSubscriptions,
                "Failed constructor leaked plugins/subscription or removed someone else's handler");
            net.UnregisterPacketHandler(FrameworkProtocol.ModuleName);
        }
        finally { AuditPlugin.Enabled = false; auth.Stop(); }
        Console.WriteLine("FRAMEWORK LIFECYCLE AUDIT RESULT passed; activation-service/startup-refusal/paired-cleanup/reentry/failed-constructor-ownership");
    }
}
