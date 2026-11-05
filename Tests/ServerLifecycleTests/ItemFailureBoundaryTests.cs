using System;
using System.Collections.Generic;
using ServerRuntime;
using Utils.Framework;

internal static class ItemFailureBoundaryTests
{
    private static int checks;
    private static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    public static void Run()
    {
        checks = 0;
        var packet = new FrameworkPacket { MessageType = "synthetic.item", PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkItemPayload { CodecId = "synthetic", PayloadBytes = new byte[] { 1 } }) };
        string logs = "";
        var context = new ServerFrameworkContext { Log = (text, _) => logs += text };
        foreach (bool interceptor in new[] { true, false })
        foreach (bool predicate in new[] { true, false })
        {
            var first = new Handler(); var later = new Handler(); var observer = new Observer(); var codec = new Codec();
            if (predicate) first.Gate = () => throw new InvalidOperationException("synthetic-private");
            else first.Body = () => throw new InvalidOperationException("synthetic-private");
            var runner = new ServerPipelineRunner(null, null, null, null, null, null, null, new[] { codec },
                interceptor ? new IServerInboundItemInterceptor[] { first } : null,
                interceptor ? new IServerDefaultItemHandler[] { later } : new IServerDefaultItemHandler[] { first, later }, new[] { observer });
            Check(runner.ProcessIncomingItem(packet, context), "Failed item request was not consumed");
            Check(later.Calls == 0 && codec.Calls == 0 && first.Calls == (predicate ? 0 : 1), "Item failure executed a later candidate or codec");
            Check(observer.Calls == 1 && observer.Terminal == ItemHandlingResultAction.StopPropagation, "Failure was not observed as stopped");
            first.Gate = () => true; first.Body = () => { };
            Check(runner.ProcessIncomingItem(packet, context) && first.Calls == (predicate ? 1 : 2), "One failed item poisoned subsequent request");
        }
        foreach (string stage in new[] { "CanDecode", "Decode", "CanEncode", "Encode", "Null" })
        {
            var bad = new Codec { Failure = stage }; var later = new Codec(); var observer = new Observer();
            var runner = new ServerPipelineRunner(null, null, null, null, null, null, null, new IItemCodec[] { bad, later }, itemObservers: new[] { observer });
            Check(runner.ProcessIncomingItem(packet, context) && later.Calls == 0, "Codec failure escaped or executed alternate codec: " + stage);
            Check(observer.Terminal == ItemHandlingResultAction.StopPropagation, "Codec failure reported a successful terminal action: " + stage);
        }
        // Diagnostics/observer failures must not escape a consumed operation or hide the next observer.
        var broken = new Observer { Throw = true }; var healthy = new Observer();
        var successful = new MessageHandler();
        var observers = new ServerPipelineRunner(null, new[] { successful }, new IServerMessageObserver[] { broken, healthy }, null, new[] { successful },
            new IServerCommandObserver[] { broken, healthy }, null, null, defaultItemHandlers: new[] { new Handler() }, itemObservers: new[] { broken, healthy });
        context.Log = (_, _) => throw new InvalidOperationException("synthetic-logger");
        Check(observers.ProcessIncomingMessage(packet, context) && observers.ProcessIncomingCommand(packet, context) && observers.ProcessIncomingItem(packet, context), "Observer/logger failure escaped");
        Check(healthy.Calls == 3, "One observer hid other observers");
        Check(!logs.Contains("synthetic-private"), "Plugin exception content leaked into diagnostics");
        Console.WriteLine($"ITEM FAILURE BOUNDARY RESULT passed; checks={checks}; request-stops/next-request-recovers/no-alternate-codec/observer-isolation");
    }
    private sealed class Handler : IServerInboundItemInterceptor, IServerDefaultItemHandler
    {
        public int Priority => 0;
        public int Calls;
        public Func<bool> Gate = () => true;
        public Action Body = () => { };
        public bool CanInterceptIncomingItem(FrameworkPacket p) => Gate();
        public bool CanHandleIncomingItem(FrameworkPacket p) => Gate();
        public ServerIncomingItemResult InterceptIncomingItem(FrameworkPacket p, ServerFrameworkContext c) => HandleIncomingItem(p,c,null);
        public ServerIncomingItemResult HandleIncomingItem(FrameworkPacket p, ServerFrameworkContext c, IReadOnlyList<IItemCodec> codecs)
        { Calls++; Body(); return new ServerIncomingItemResult { Action = ItemHandlingResultAction.Handled }; }
    }
    private sealed class Codec : IItemCodec
    {
        public string CodecId => "synthetic";
        public string Failure;
        public int Calls;
        private void Probe(string name) { Calls++; if (Failure == name) throw new InvalidOperationException("synthetic-private"); }
        public bool CanDecode(FrameworkItemPayload p, ItemCodecContext c) { Probe("CanDecode"); return true; }
        public object Decode(FrameworkItemPayload p, ItemCodecContext c) { Probe("Decode"); return Failure == "Null" ? null : new object(); }
        public bool CanEncode(object o, ItemCodecContext c) { Probe("CanEncode"); return true; }
        public FrameworkItemPayload Encode(object o, ItemCodecContext c) { Probe("Encode"); return new FrameworkItemPayload(); }
    }
    private sealed class Observer : IServerItemObserver, IServerMessageObserver, IServerCommandObserver
    {
        public int Priority => 0;
        public int Calls; public bool Throw; public ItemHandlingResultAction Terminal;
        public bool CanObserveIncomingItem(FrameworkPacket p) => true;
        public bool CanObserveIncomingMessage(FrameworkPacket p) => true;
        public bool CanObserveIncomingCommand(FrameworkPacket p) => true;
        private void Observe() { if (Throw) throw new InvalidOperationException("synthetic-private"); Calls++; }
        public void ObserveIncomingItem(FrameworkPacket p, ServerFrameworkContext c, ItemHandlingResultAction a) { Terminal = a; Observe(); }
        public void ObserveIncomingMessage(FrameworkPacket p, ServerFrameworkContext c, MessageHandlingResultAction a) => Observe();
        public void ObserveIncomingCommand(FrameworkPacket p, ServerFrameworkContext c, MessageHandlingResultAction a) => Observe();
    }
    private sealed class MessageHandler : IServerDefaultMessageHandler, IServerDefaultCommandHandler
    {
        public int Priority => 0;
        public bool CanHandleIncomingMessage(FrameworkPacket p) => true;
        public bool CanHandleIncomingCommand(FrameworkPacket p) => true;
        public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket p, ServerFrameworkContext c) => new() { Action = MessageHandlingResultAction.Handled };
        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket p, ServerFrameworkContext c) => new() { Action = MessageHandlingResultAction.Handled };
    }
}
