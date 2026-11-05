using System;
using System.Collections.Generic;
using Connections;
using PhinixServer.Framework;
using ServerRuntime;
using Utils.Framework;

internal static class DispatchOutcomeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ServerPipelineRunner Runner(IServerOutboundPacketInterceptor interceptor = null,
        IServerDefaultCommandHandler[] commands = null, IServerDefaultMessageHandler[] messages = null) =>
        new ServerPipelineRunner(null, messages, null, null, commands, null,
            interceptor == null ? null : new[] { interceptor }, null);
    private static FrameworkPacket Packet() => new FrameworkPacket { MessageType = "synthetic", PayloadJson = "{}" };
    public static void Run()
    {
        var dispatcher = new FrameworkServerPacketDispatcher();
        Check(dispatcher.SendWithResult("a", Packet()).For("a") == TransportSendOutcome.NotSent, "Unconfigured dispatcher must refuse");
        int calls = 0;
        dispatcher.Configure((_, _, _) => calls++);
        Check(dispatcher.SendWithResult("a", Packet()).For("a") == TransportSendOutcome.Unknown && calls == 1,
            "Void callback cannot assert submission");
        dispatcher.ConfigureWithResult((id, _, _) => PacketDispatchResult.Single(id, TransportSendOutcome.Submitted));
        var services = new ExtensionHostContext();
        services.AddService<IResultServerPacketDispatcher>(dispatcher);
        Check(services.GetRequiredService<IResultServerPacketDispatcher>().SendWithResult("a", Packet()).For("a") == TransportSendOutcome.Submitted,
            "Plugin service boundary lost typed outcome");
        dispatcher.ConfigureWithResult((_, _, _) => throw new Exception("private-marker"));
        Check(dispatcher.SendWithResult("a", Packet()).For("a") == TransportSendOutcome.Unknown, "Throw must not turn into NotSent");

        var context = new ServerOutboundPacketContext { TargetConnectionIds = new[] { "a" }, IsConnectionFrameworkCapable = _ => false };
        var result = Runner().DispatchOutboundWithResult(Packet(), context, (_, _) => { calls++; return TransportSendOutcome.Submitted; });
        Check(result.For("a") == TransportSendOutcome.NotSent && calls == 1, "Capability refusal handed off data");
        var bypass = new Interceptor((_, c) => { c.DeliverToConnection("a", Packet()); return new ServerOutgoingPacketResult { Action = MessageHandlingResultAction.StopPropagation }; });
        Check(Runner(bypass).DispatchOutboundWithResult(Packet(), context, (_, _) => { calls++; return TransportSendOutcome.Submitted; }).For("a") == TransportSendOutcome.NotSent && calls == 1,
            "Interceptor bypassed capability gate");
        context.IsConnectionFrameworkCapable = _ => true;
        context.ConnectionHasCapability = (_, _) => false;
        Check(Runner().DispatchOutboundWithResult(Packet(), context, (_, _) => throw new Exception()).For("a") == TransportSendOutcome.NotSent,
            "Missing message capability attempted handoff");
        context.ConnectionHasCapability = null;
        var redirect = new Interceptor((_, _) => new ServerOutgoingPacketResult {
            Action = MessageHandlingResultAction.Replace, TargetConnectionIds = new[] { "b", "b" } });
        calls = 0;
        result = Runner(redirect).DispatchOutboundWithResult(Packet(), context, (id, _) => { Check(id == "b", "Retarget ignored"); calls++; return TransportSendOutcome.Submitted; });
        Check(result.For("a") == TransportSendOutcome.NotSent && result.For("b") == TransportSendOutcome.Submitted && calls == 1 && result.HasPossibleDelivery,
            "Retarget/duplicate result mismatch");
        string logs = ""; context.Log = (text, _) => logs += text;
        var throwing = new Interceptor((_, c) => { c.DeliverToConnection("a", Packet()); throw new Exception("private-marker"); });
        calls = 0;
        result = Runner(throwing).DispatchOutboundWithResult(Packet(), context, (_, _) => { calls++; return TransportSendOutcome.Submitted; });
        Check(calls == 1 && result.For("a") == TransportSendOutcome.Unknown && result.HasUncertainFailure && result.For("unreported") == TransportSendOutcome.Unknown && !logs.Contains("private-marker"),
            "Interceptor throw retried or leaked private exception");
        var blocked = new Interceptor((_, _) => new ServerOutgoingPacketResult { Action = MessageHandlingResultAction.StopPropagation });
        calls = 0;
        Check(Runner(blocked).DispatchOutboundWithResult(Packet(), context, (_, _) => { calls++; return TransportSendOutcome.Submitted; }).For("a") == TransportSendOutcome.NotSent && calls == 0,
            "Blocked packet sent");
        Action<string, FrameworkPacket> late = null;
        var retaining = new Interceptor((_, c) => { late = c.DeliverToConnection; return new ServerOutgoingPacketResult { Action = MessageHandlingResultAction.StopPropagation }; });
        result = Runner(retaining).DispatchOutboundWithResult(Packet(), context, (_, _) => { calls++; return TransportSendOutcome.Submitted; });
        late("a", Packet());
        Check(calls == 0 && result.For("a") == TransportSendOutcome.NotSent, "Late callback sent after report publication");
        context.TargetConnectionIds = new[] { "a", "b" };
        result = Runner().DispatchOutboundWithResult(Packet(), context, (id, _) => id == "a" ? throw new Exception() : TransportSendOutcome.Submitted);
        Check(result.For("a") == TransportSendOutcome.Unknown && result.For("b") == TransportSendOutcome.Submitted, "Recipient outcomes conflated");

        int first = 0, second = 0;
        var failed = new Handler(() => throw new Exception("private-marker"), () => first++);
        var later = new Handler(() => true, () => second++);
        var inbound = new ServerFrameworkContext { Log = (text, _) => logs += text };
        var runner = Runner(commands: new IServerDefaultCommandHandler[] { failed, later }, messages: new IServerDefaultMessageHandler[] { failed, later });
        Check(runner.ProcessIncomingCommand(Packet(), inbound) && runner.ProcessIncomingMessage(Packet(), inbound) && first == 0 && second == 0,
            "Failed authorization predicate escaped or allowed next handler");
        failed.Gate = () => true;
        failed.Body = () => { first++; throw new Exception("private-marker"); };
        Check(runner.ProcessIncomingCommand(Packet(), inbound) && runner.ProcessIncomingMessage(Packet(), inbound) && first == 2 && second == 0,
            "Unknown handler effects fell through to second business handler");
        failed.Gate = () => false;
        Check(runner.ProcessIncomingCommand(Packet(), inbound) && runner.ProcessIncomingMessage(Packet(), inbound) && second == 2,
            "Failed request poisoned later independent requests");
        Check(!logs.Contains("private-marker"), "Inbound failure logs leaked exception contents");
        Console.WriteLine("DISPATCH/GATE RESULT passed; dispatcher/filter/retarget/unknown/no-retry/predicate/handler isolation");
    }
    private sealed class Interceptor : IServerOutboundPacketInterceptor
    {
        private readonly Func<FrameworkPacket, ServerOutboundPacketContext, ServerOutgoingPacketResult> action;
        public Interceptor(Func<FrameworkPacket, ServerOutboundPacketContext, ServerOutgoingPacketResult> action) { this.action = action; }
        public int Priority => 0;
        public bool CanInterceptOutgoingPacket(FrameworkPacket p, ServerOutboundPacketContext c) => true;
        public ServerOutgoingPacketResult InterceptOutgoingPacket(FrameworkPacket p, ServerOutboundPacketContext c) => action(p, c);
    }
    private sealed class Handler : IServerDefaultCommandHandler, IServerDefaultMessageHandler
    {
        public Func<bool> Gate;
        public Action Body;
        public Handler(Func<bool> gate, Action body) { Gate = gate; Body = body; }
        public int Priority => 0;
        public bool CanHandleIncomingCommand(FrameworkPacket p) => Gate();
        public bool CanHandleIncomingMessage(FrameworkPacket p) => Gate();
        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket p, ServerFrameworkContext c) { Body(); return new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Handled }; }
        public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket p, ServerFrameworkContext c) { Body(); return new ServerIncomingMessageResult { Action = MessageHandlingResultAction.Handled }; }
    }
}
