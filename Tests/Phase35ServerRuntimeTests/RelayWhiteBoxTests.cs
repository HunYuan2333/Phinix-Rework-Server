using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Utils.Framework;

internal static partial class Program
{
    private sealed class RelayLifecyclePolicy : IExtensionDiscoveryPolicy
    {
        public bool ShouldScanAssembly(Assembly assembly) => assembly == typeof(Program).Assembly;
        public bool ShouldDiscoverType(Type type) => type == typeof(RelayRegisterFailure) || type == typeof(RelayActivationFailure);
    }
    [PhinixExtension("test.relay-register-failure")]
    public sealed class RelayRegisterFailure : IPhinixExtensionModule
    {
        public string ExtensionId => "test.relay-register-failure";
        public void Register(IExtensionBuilder builder)
        {
            builder.HostContext.GetRequiredService<IClientCommandCapabilities>().Declare(ExtensionId, "test.relay", "register");
            throw new InvalidOperationException("Synthetic registration failure");
        }
    }
    [PhinixExtension("test.relay-activation-failure")]
    public sealed class RelayActivationFailure : IPhinixExtensionModule, IActivatablePhinixExtensionModule
    {
        public string ExtensionId => "test.relay-activation-failure";
        public void Register(IExtensionBuilder builder) { }
        public void Activate(ExtensionHostContext host)
        {
            host.GetRequiredService<IClientCommandCapabilities>().Declare(ExtensionId, "test.relay", "activate");
            throw new InvalidOperationException("Synthetic activation failure");
        }
        public void Shutdown(ExtensionHostContext host) { }
    }
    private static void AssertRelayWhiteBoxLifecycle()
    {
        var declarations = new ClientCommandCapabilities();
        var tasks = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 250; i++)
                using (declarations.Declare("worker-" + worker, "test.concurrent", "event"))
                    if (declarations.Capture("e").Capabilities.Count != 1)
                        throw new Exception("Concurrent duplicate capability projection changed.");
        })).ToArray();
        Assert(Task.WaitAll(tasks, 10000), "Concurrent declaration owners finish within deadline.");
        Assert(declarations.GetSnapshot().Count == 0, "Concurrent declaration disposal leaves no capability.");
        var host = new ExtensionHostContext();
        host.AddService<IClientCommandCapabilities>(declarations);
        host.AddService<IExtensionDiscoveryPolicy>(new RelayLifecyclePolicy());
        var extensions = PhinixExtensionRegistry.DiscoverExtensions(host);
        Assert(declarations.GetSnapshot().Count == 0, "Real registry rollback revokes declaration after failed Register.");
        PhinixExtensionRegistry.ActivateExtensions(extensions, host);
        Assert(declarations.GetSnapshot().Count == 0, "Real registry rollback revokes declaration after failed Activate.");
        PhinixExtensionRegistry.ShutdownExtensions(extensions, host);
        Assert(declarations.GetSnapshot().Count == 0, "Shutdown remains idempotent after partial module failure.");
    }
}
