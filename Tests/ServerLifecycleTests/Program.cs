using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Authentication;
using Connections;
using PhinixServer;
using ServerRuntime;

internal static class Program
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-config")
        {
            new Config { Address = System.Net.IPAddress.Any, Port = 0, SaveInterval = 60000,
                LogPath = "server.log", UserDatabasePath = "users", CredentialDatabasePath = "credentials" }.Save(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--trade-config") { TradeConfigTests.Run(); return; }
        if (args.Length == 1 && args[0] == "--pinned-udp-synchronization") { PinnedUdpSynchronizationTests.Run(); return; }
        if (args.Length == 1 && args[0] == "--send-outcome") { SendOutcomeTests.Basic(); return; }
        if (args.Length == 1 && args[0] == "--send-network") { SendOutcomeTests.Network(); return; }
        if (args.Length == 1 && args[0] == "--config-validation") { StartupValidationTests.Run(); return; }
        if (args.Length == 1 && args[0] == "--ready-network") { ReadyNetworkTests.Run(); return; }
        if (args.Length == 2 && args[0] == "--write-control-fixture") { AdmissionControlTests.WriteFixture(args[1]); return; }
        if (args.Length == 4 && args[0] == "--p3-wire") { P3NetworkProbe.Run(int.Parse(args[1]), args[2], args[3]); return; }
        if (args.Length == 1 && args[0] == "--plugin-result-boundary") { PluginResultBoundaryTests.Run(); return; }
        if (args.Length == 3 && args[0] == "--p5-trade-mixed") { P3NetworkProbe.RunTradeMixed(int.Parse(args[1]), args[2]); return; }
        if (args.Length == 3 && args[0] == "--p5-mixed") { P3NetworkProbe.RunMixed(int.Parse(args[1]), args[2]); return; }
        if (args.Length == 1 && args[0] == "--item-failure-boundary") { ItemFailureBoundaryTests.Run(); return; }
        if (args.Length == 1 && args[0] == "--udp-receive-budget") { UdpReceiveBudgetTests.Run(); return; }
        if (args.Length == 1 && args[0] == "--udp-event-audit") { UdpEventIsolationTests.Run(); return; }
        if (args.Length == 1 && args[0] == "--udp-bind-policy") { UdpEventIsolationTests.BindPolicy(); return; }
        if (args.Length == 1 && args[0] == "--framework-lifecycle-audit") { FrameworkLifecycleAuditTests.Run(); return; }
        var calls = new List<string>();
        var lifecycle = new ServerSaveLifecycle(() => calls.Add("save"));
        Require(lifecycle.TrySave(), "periodic save");
        lifecycle.Stop(() => calls.Add("quiesce"));
        lifecycle.Stop(() => throw new Exception("repeated shutdown"));
        Require(!lifecycle.TrySave() && string.Join(",", calls) == "save,quiesce,save", "one final save after quiesce; no saves after stop");
        Require(lifecycle.State == ServerSaveState.Stopped, "stopped state");

        int aborts = 0;
        var startup = new ServerSaveLifecycle(() => throw new Exception("Startup failure must never save."));
        startup.StopWithoutSave(() => aborts++);
        startup.Stop(() => throw new Exception("Aborted startup must not quiesce twice."));
        Require(aborts == 1 && !startup.TrySave() && startup.State == ServerSaveState.Stopped,
            "Startup abort quiesces once and disables periodic and final saves.");

        int attempts = 0;
        var failing = new ServerSaveLifecycle(() => { if (++attempts == 1) throw new IOException("synthetic"); });
        try { failing.TrySave(); throw new Exception("save failure swallowed"); } catch (IOException) { }
        Require(failing.State == ServerSaveState.SaveFailed && failing.TrySave(), "failed save visible, later periodic retry allowed");
        failing.Stop(() => { });

        int finalAttempts = 0;
        var failedStop = new ServerSaveLifecycle(() => { finalAttempts++; throw new IOException("synthetic"); });
        try { failedStop.Stop(() => { }); throw new Exception("shutdown failure swallowed"); } catch (IOException) { }
        Require(failedStop.State == ServerSaveState.StopFailed && !failedStop.TrySave(), "failed shutdown never reported stopped");
        try { failedStop.Stop(() => { }); throw new Exception("stop failure erased"); } catch (InvalidOperationException) { }
        Require(finalAttempts == 1, "no implicit destructive retry after failed final save");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int saves = 0, simultaneous = 0, maximum = 0;
        var serial = new ServerSaveLifecycle(() =>
        {
            int active = Interlocked.Increment(ref simultaneous);
            maximum = Math.Max(maximum, active);
            if (Interlocked.Increment(ref saves) == 1) { entered.Set(); Require(release.Wait(5000), "save barrier"); }
            Interlocked.Decrement(ref simultaneous);
        });
        Task periodic = Task.Run(() => serial.TrySave());
        Require(entered.Wait(5000), "entered first save");
        Require(!serial.TrySave(), "overlapping timer tick coalesced without queueing");
        Task stop = Task.Run(() => serial.Stop(() => { }));
        Require(SpinWait.SpinUntil(() => serial.StopRequested, 5000), "stop admission flag");
        Task<bool> queued = Task.Run(() => serial.TrySave());
        release.Set();
        Require(Task.WaitAll(new Task[] { periodic, stop, queued }, 5000), "bounded drain");
        Require(!queued.Result && saves == 2 && maximum == 1, "timer/final saves serialized; queued work rejected");

        // Exercise the actual host composition root and scoped service identities, not a parallel test-only registration.
        var composition = typeof(Config).Assembly.GetType("PhinixServer.ServerComposition");
        using (var container = (IContainer)composition.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Config { Port = 0 } }))
        {
            NetServer first;
            using (var scope = container.BeginLifetimeScope())
            {
                first = scope.Resolve<NetServer>();
                Require(ReferenceEquals(scope.Resolve<ServerStorage.IOperationJournalFactory>(), scope.Resolve<ServerStorage.IOperationJournalFactory>()), "same scoped neutral journal factory");
                Require(ReferenceEquals(first, scope.Resolve<NetServer>()), "same scope same transport");
                Require(ReferenceEquals(first, scope.Resolve<IServerTransport>()), "neutral transport boundary shares the existing UDP instance");
                using (var workerStarted = new ManualResetEventSlim())
                using (var workerRelease = new ManualResetEventSlim())
                {
                    var worker = new Thread(() => { workerStarted.Set(); workerRelease.Wait(); }) { IsBackground = true };
                    var field = typeof(NetServer).GetField("pollThread", BindingFlags.Instance | BindingFlags.NonPublic);
                    worker.Start();
                    Require(workerStarted.Wait(5000), "synthetic worker started");
                    field.SetValue(first, worker);
                    try
                    {
                        try { first.Stop(); throw new Exception("worker timeout ignored"); } catch (TimeoutException) { }
                        Require(ReferenceEquals(field.GetValue(first), worker), "timed-out worker retained for cleanup");
                    }
                    finally { workerRelease.Set(); worker.Join(5000); }
                    first.Stop();
                    Require(field.GetValue(first) == null, "confirmed worker joined and cleared");
                }
                Require(ReferenceEquals(scope.Resolve<ServerAuthenticator>(), scope.Resolve<ServerAuthenticator>()), "same scoped authenticator");
            }
            using (var scope = container.BeginLifetimeScope())
                Require(!ReferenceEquals(first, scope.Resolve<NetServer>()), "new application scope owns new transport");
        }
        PinnedUdpSynchronizationTests.Run();
        UdpReceiveBudgetTests.Run();
        TradeConfigTests.Run();
        WorkQueueTests.Run();
        SendOutcomeTests.Basic();
        DispatchOutcomeTests.Run();
        PluginResultBoundaryTests.Run();
        ItemFailureBoundaryTests.Run();
        UdpEventIsolationTests.Run();
        UdpEventIsolationTests.BindPolicy();
        FrameworkLifecycleAuditTests.Run();
        ServerSessionTests.Run();
        AdmissionControlTests.Run();
        StartupValidationTests.Run();
        Console.WriteLine("LIFECYCLE RESULT passed; scenarios=6; serializedSave=true; scopedComposition=true");
    }
}
