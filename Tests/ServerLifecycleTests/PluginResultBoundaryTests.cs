using System;
using System.Collections.Generic;
using System.Reflection;
using PhinixServer.Framework;
using ServerRuntime;
using Utils.Framework;

internal static class PluginResultBoundaryTests
{
    private static int checks;
    private static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    private static T Bind<T>(string name, object plugin) => (T)Activator.CreateInstance(
        typeof(PhinixFrameworkServer).GetNestedType(name, BindingFlags.NonPublic), plugin, "synthetic.plugin");
    public static void Run()
    {
        checks = 0;
        var plugin = new Plugin();
        var packet = new FrameworkPacket { MessageType = "synthetic.test" };
        int warnings = 0;
        var context = new ServerFrameworkContext { Log = (_, _) => warnings++ };
        var mi = Bind<IServerInboundMessageInterceptor>("BoundServerInboundMessageInterceptor", plugin);
        var mh = Bind<IServerDefaultMessageHandler>("BoundServerDefaultMessageHandler", plugin);
        var ci = Bind<IServerInboundCommandInterceptor>("BoundServerInboundCommandInterceptor", plugin);
        var ch = Bind<IServerDefaultCommandHandler>("BoundServerDefaultCommandHandler", plugin);
        var ii = Bind<IServerInboundItemInterceptor>("BoundServerInboundItemInterceptor", plugin);
        var ih = Bind<IServerDefaultItemHandler>("BoundServerDefaultItemHandler", plugin);
        foreach (var action in new[] { MessageHandlingResultAction.Continue, MessageHandlingResultAction.Handled,
            MessageHandlingResultAction.Replace, MessageHandlingResultAction.StopPropagation, MessageHandlingResultAction.SuppressDefault,
            MessageHandlingResultAction.Observe, MessageHandlingResultAction.LegacyFallback })
        {
            plugin.Message = new ServerIncomingMessageResult { Action = action, Message = packet };
            plugin.Command = new ServerIncomingCommandResult { Action = action, Command = packet };
            foreach (var result in new[] { mi.InterceptIncomingMessage(packet, context), mh.HandleIncomingMessage(packet, context) })
                Check(action == MessageHandlingResultAction.LegacyFallback
                    ? result.Action == MessageHandlingResultAction.Continue && result.Message == packet && !ReferenceEquals(result, plugin.Message)
                    : ReferenceEquals(result, plugin.Message), "Message result translation/identity changed");
            foreach (var result in new[] { ci.InterceptIncomingCommand(packet, context), ch.HandleIncomingCommand(packet, context) })
                Check(action == MessageHandlingResultAction.LegacyFallback
                    ? result.Action == MessageHandlingResultAction.Continue && result.Command == packet && !ReferenceEquals(result, plugin.Command)
                    : ReferenceEquals(result, plugin.Command), "Command result translation/identity changed");
            Check(plugin.Message.Action == action && plugin.Command.Action == action, "Mutated retained plugin result");
        }
        foreach (var action in new[] { ItemHandlingResultAction.Continue, ItemHandlingResultAction.Handled,
            ItemHandlingResultAction.ReplacePayload, ItemHandlingResultAction.StopPropagation,
            ItemHandlingResultAction.SuppressDefault, ItemHandlingResultAction.LegacyFallback })
        {
            var payload = new FrameworkItemPayload(); var decoded = new object(); var error = new Exception("synthetic-private");
            plugin.Item = new ServerIncomingItemResult { Action = action, Item = packet, ReplacedPayload = payload,
                DecodedItem = decoded, HandledByHandlerId = "synthetic", FailureReason = "synthetic-private", FailureException = error };
            foreach (var result in new[] { ii.InterceptIncomingItem(packet, context), ih.HandleIncomingItem(packet, context, Array.Empty<IItemCodec>()) })
                Check(action == ItemHandlingResultAction.LegacyFallback
                    ? result.Action == ItemHandlingResultAction.Continue && result.Item == packet && result.ReplacedPayload == payload &&
                        result.DecodedItem == decoded && result.FailureException == error && result.FailureReason == "synthetic-private" &&
                        result.HandledByHandlerId == "synthetic" && !ReferenceEquals(result, plugin.Item)
                    : ReferenceEquals(result, plugin.Item), "Item result metadata/identity lost");
            Check(plugin.Item.Action == action, "Mutated retained item result");
        }
        Check(warnings == 6, "Non-obsolete results produced warning or obsolete result lost warning");
        Check(context.SourceExtensionId == "synthetic.plugin", "Extension attribution lost");
        Check(mi.Priority == 17 && mh.Priority == 17 && ci.Priority == 17 && ch.Priority == 17 && ii.Priority == 17 && ih.Priority == 17,
            "Plugin priority changed");
        Check(mi.CanInterceptIncomingMessage(packet) && mh.CanHandleIncomingMessage(packet) && ci.CanInterceptIncomingCommand(packet) &&
            ch.CanHandleIncomingCommand(packet) && ii.CanInterceptIncomingItem(packet) && ih.CanHandleIncomingItem(packet), "Predicate forwarding changed");
        plugin.Message = null; plugin.Command = null; plugin.Item = null;
        Check(mi.InterceptIncomingMessage(packet, context) == null && mh.HandleIncomingMessage(packet, context) == null &&
            ci.InterceptIncomingCommand(packet, context) == null && ch.HandleIncomingCommand(packet, context) == null &&
            ii.InterceptIncomingItem(packet, context) == null && ih.HandleIncomingItem(packet, context, null) == null, "Null result changed");
        plugin.Message = new ServerIncomingMessageResult { Action = MessageHandlingResultAction.LegacyFallback };
        plugin.Command = new ServerIncomingCommandResult { Action = MessageHandlingResultAction.LegacyFallback };
        plugin.Item = new ServerIncomingItemResult { Action = ItemHandlingResultAction.LegacyFallback };
        context.Log = (_, _) => throw new Exception("synthetic-logger");
        Check(mi.InterceptIncomingMessage(packet, context).Action == MessageHandlingResultAction.Continue &&
            ch.HandleIncomingCommand(packet, context).Action == MessageHandlingResultAction.Continue &&
            ii.InterceptIncomingItem(packet, context).Action == ItemHandlingResultAction.Continue, "Diagnostic failure escaped normalization");
        // The actual composition wrappers feed a neutral pipeline; this is not a second pipeline implementation.
        var runner = new ServerPipelineRunner(new[] { mi }, new[] { mh }, null, new[] { ci }, new[] { ch }, null, null, null,
            new[] { ii }, new[] { ih });
        Check(runner.ProcessIncomingMessage(packet, context) && runner.ProcessIncomingCommand(packet, context) && runner.ProcessIncomingItem(packet, context),
            "Obsolete plugin stopped pipeline before default handler");
        plugin.Throw = true;
        try { mi.InterceptIncomingMessage(packet, context); throw new Exception("Plugin exception swallowed"); }
        catch (InvalidOperationException) { Check(true, "Plugin failure preserved"); }
        Console.WriteLine($"PLUGIN BOUNDARY RESULT passed; checks={checks}; six real composition wrappers; no game assets");
    }
    private sealed class Plugin : IServerInboundMessageInterceptor, IServerDefaultMessageHandler,
        IServerInboundCommandInterceptor, IServerDefaultCommandHandler, IServerInboundItemInterceptor, IServerDefaultItemHandler
    {
        public int Priority => 17;
        public bool Throw;
        public ServerIncomingMessageResult Message;
        public ServerIncomingCommandResult Command;
        public ServerIncomingItemResult Item;
        public bool CanInterceptIncomingMessage(FrameworkPacket p) => true;
        public bool CanHandleIncomingMessage(FrameworkPacket p) => true;
        public bool CanInterceptIncomingCommand(FrameworkPacket p) => true;
        public bool CanHandleIncomingCommand(FrameworkPacket p) => true;
        public bool CanInterceptIncomingItem(FrameworkPacket p) => true;
        public bool CanHandleIncomingItem(FrameworkPacket p) => true;
        public ServerIncomingMessageResult InterceptIncomingMessage(FrameworkPacket p, ServerFrameworkContext c)
        { if (Throw) throw new InvalidOperationException("synthetic"); return Message; }
        public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket p, ServerFrameworkContext c) => Message;
        public ServerIncomingCommandResult InterceptIncomingCommand(FrameworkPacket p, ServerFrameworkContext c) => Command;
        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket p, ServerFrameworkContext c) => Command;
        public ServerIncomingItemResult InterceptIncomingItem(FrameworkPacket p, ServerFrameworkContext c) => Item;
        public ServerIncomingItemResult HandleIncomingItem(FrameworkPacket p, ServerFrameworkContext c, IReadOnlyList<IItemCodec> codecs) => Item;
    }
}
