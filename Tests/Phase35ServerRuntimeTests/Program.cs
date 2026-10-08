using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Google.Protobuf;
using Utils;
using Utils.Framework;
using System.Threading;
using ServerRuntime;
internal static partial class Program
{
    private static int assertions;
    private static int Main() { try {
        AssertPreHandleInterceptorCanRewriteMessageBeforeDefaultHandler();
        AssertPreHandleInterceptorCanBlockDefaultHandler();
        AssertPreHandleCommandInterceptorCanBlockDefaultHandler();
        AssertOutboundInterceptorCanFilterRecipientsAndRewritePacket();
        AssertOutboundInterceptorExceptionDoesNotBlockDelivery();
        AssertMessageObserverExceptionDoesNotBlockLaterObservers();
        AssertCommandObserverExceptionDoesNotBlockLaterObservers();
        AssertLegacyApisRemoved();
        Console.WriteLine("PASS ServerFull 8 cases; " + assertions + " assertions"); return 0;
    } catch(Exception error) { Console.Error.WriteLine(error); return 1; } }
    private static void AssertPreHandleInterceptorCanRewriteMessageBeforeDefaultHandler()
    {
        string observedPayload = null;
        PipelineTestHarness pipeline = new PipelineTestHarness();
        ServerPipelineRunner runner = pipeline.Runner;
        pipeline.InboundMessageInterceptors.Add(new DelegateServerInboundMessageInterceptor(
            canIntercept: message => message.MessageType == "chat",
            handle: (message, context) => new ServerIncomingMessageResult
            {
                Action = MessageHandlingResultAction.Replace,
                Message = new FrameworkPacket
                {
                    Kind = message.Kind,
                    Flow = message.Flow,
                    MessageType = message.MessageType,
                    SenderUuid = message.SenderUuid,
                    SessionId = message.SessionId,
                    PayloadJson = "rewritten"
                }
            }));
        pipeline.DefaultMessageHandlers.Add(new DelegateServerDefaultMessageHandler(
            canHandle: message => message.MessageType == "chat",
            handle: (message, context) =>
            {
                observedPayload = message.PayloadJson;
                return new ServerIncomingMessageResult { Action = MessageHandlingResultAction.Handle };
            }));

        runner.ProcessIncomingMessage(
            new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindMessage,
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = "chat",
                SenderUuid = "sender",
                SessionId = "session",
                PayloadJson = "original"
            },
            CreateServerContext());

        Assert(observedPayload == "rewritten", "Pre-handle interceptor should rewrite the message before default processing.");
    }
    private static void AssertPreHandleInterceptorCanBlockDefaultHandler()
    {
        bool defaultHandlerCalled = false;
        PipelineTestHarness pipeline = new PipelineTestHarness();
        ServerPipelineRunner runner = pipeline.Runner;
        pipeline.InboundMessageInterceptors.Add(new DelegateServerInboundMessageInterceptor(
            canIntercept: message => true,
            handle: (message, context) => new ServerIncomingMessageResult
            {
                Action = MessageHandlingResultAction.Block
            }));
        pipeline.DefaultMessageHandlers.Add(new DelegateServerDefaultMessageHandler(
            canHandle: message => true,
            handle: (message, context) =>
            {
                defaultHandlerCalled = true;
                return new ServerIncomingMessageResult { Action = MessageHandlingResultAction.Handle };
            }));

        runner.ProcessIncomingMessage(
            new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindMessage,
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = "chat"
            },
            CreateServerContext());

        Assert(!defaultHandlerCalled, "Blocked inbound message should not reach default handlers.");
    }
    private static void AssertPreHandleCommandInterceptorCanBlockDefaultHandler()
    {
        bool defaultHandlerCalled = false;
        PipelineTestHarness pipeline = new PipelineTestHarness();
        ServerPipelineRunner runner = pipeline.Runner;
        pipeline.InboundCommandInterceptors.Add(new DelegateServerInboundCommandInterceptor(
            canIntercept: command => true,
            handle: (command, context) => new ServerIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Block
            }));
        pipeline.DefaultCommandHandlers.Add(new DelegateServerDefaultCommandHandler(
            canHandle: command => true,
            handle: (command, context) =>
            {
                defaultHandlerCalled = true;
                return new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Handle };
            }));

        runner.ProcessIncomingCommand(
            new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindCommand,
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                MessageType = "trade.request"
            },
            CreateServerContext());

        Assert(!defaultHandlerCalled, "Blocked inbound command should not reach default handlers.");
    }
    private static void AssertOutboundInterceptorCanFilterRecipientsAndRewritePacket()
    {
        List<(string ConnectionId, FrameworkPacket Packet)> sentPackets = new List<(string ConnectionId, FrameworkPacket Packet)>();
        PipelineTestHarness pipeline = new PipelineTestHarness();
        ServerPipelineRunner runner = pipeline.Runner;
        pipeline.OutboundPacketInterceptors.Add(new DelegateServerOutboundPacketInterceptor(
            canIntercept: packet => packet.MessageType == "chat",
            handle: (packet, context) => new ServerOutgoingPacketResult
            {
                Action = MessageHandlingResultAction.Replace,
                Packet = new FrameworkPacket
                {
                    Kind = packet.Kind,
                    Flow = packet.Flow,
                    MessageType = packet.MessageType,
                    PayloadJson = "outbound-rewritten"
                },
                TargetConnectionIds = context.TargetConnectionIds.Where(id => id != "blocked").ToArray()
            }));

        runner.DispatchOutbound(
            new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindMessage,
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = "chat",
                PayloadJson = "outbound-original"
            },
            new ServerOutboundPacketContext
            {
                TargetConnectionIds = new[] { "allowed", "blocked" },
                DeliverToConnection = (connectionId, packet) => sentPackets.Add((connectionId, packet))
            });

        Assert(sentPackets.Count == 1, "Outbound interceptor should be able to filter broadcast recipients.");
        Assert(sentPackets[0].ConnectionId == "allowed", "Only the allowed recipient should receive the outbound packet.");
        Assert(sentPackets[0].Packet.PayloadJson == "outbound-rewritten", "Outbound interceptor should be able to rewrite the outbound packet.");
    }
    private static void AssertOutboundInterceptorExceptionDoesNotBlockDelivery()
    {
        bool delivered = false;
        bool logged = false;
        PipelineTestHarness pipeline = new PipelineTestHarness();
        ServerPipelineRunner runner = pipeline.Runner;
        pipeline.OutboundPacketInterceptors.Add(new DelegateServerOutboundPacketInterceptor(
            canIntercept: packet => true,
            handle: (packet, context) => throw new InvalidOperationException("boom")));

        runner.DispatchOutbound(
            new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindMessage,
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = "chat"
            },
            new ServerOutboundPacketContext
            {
                TargetConnectionIds = new[] { "connection-1" },
                DeliverToConnection = (_, __) => delivered = true,
                Log = (_, level) => logged = level == Utils.LogLevel.ERROR
            });

        Assert(delivered, "Throwing outbound interceptor should not block delivery.");
        Assert(logged, "Throwing outbound interceptor should be logged.");
    }
    private static void AssertMessageObserverExceptionDoesNotBlockLaterObservers()
    {
        bool laterObserverCalled = false;
        bool logged = false;
        PipelineTestHarness pipeline = new PipelineTestHarness();
        ServerPipelineRunner runner = pipeline.Runner;
        pipeline.DefaultMessageHandlers.Add(new DelegateServerDefaultMessageHandler(
            canHandle: _ => true,
            handle: (_, __) => new ServerIncomingMessageResult { Action = MessageHandlingResultAction.Handle }));
        pipeline.MessageObservers.Add(new DelegateServerMessageObserver(
            canObserve: _ => true,
            observe: (_, __, ___) => throw new InvalidOperationException("boom")));
        pipeline.MessageObservers.Add(new DelegateServerMessageObserver(
            canObserve: _ => true,
            observe: (_, __, ___) => laterObserverCalled = true));

        ServerFrameworkContext context = CreateServerContext();
        context.Log = (_, level) => logged = level == Utils.LogLevel.ERROR;

        runner.ProcessIncomingMessage(
            new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindMessage,
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = "chat"
            },
            context);

        Assert(laterObserverCalled, "Throwing message observer should not block later observers.");
        Assert(logged, "Throwing message observer should be logged.");
    }
    private static void AssertCommandObserverExceptionDoesNotBlockLaterObservers()
    {
        bool laterObserverCalled = false;
        bool logged = false;
        PipelineTestHarness pipeline = new PipelineTestHarness();
        ServerPipelineRunner runner = pipeline.Runner;
        pipeline.DefaultCommandHandlers.Add(new DelegateServerDefaultCommandHandler(
            canHandle: _ => true,
            handle: (_, __) => new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Handle }));
        pipeline.CommandObservers.Add(new DelegateServerCommandObserver(
            canObserve: _ => true,
            observe: (_, __, ___) => throw new InvalidOperationException("boom")));
        pipeline.CommandObservers.Add(new DelegateServerCommandObserver(
            canObserve: _ => true,
            observe: (_, __, ___) => laterObserverCalled = true));

        ServerFrameworkContext context = CreateServerContext();
        context.Log = (_, level) => logged = level == Utils.LogLevel.ERROR;

        runner.ProcessIncomingCommand(
            new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindCommand,
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                MessageType = "trade.request"
            },
            context);

        Assert(laterObserverCalled, "Throwing command observer should not block later observers.");
        Assert(logged, "Throwing command observer should be logged.");
    }
    private static void AssertLegacyApisRemoved()
    {
        string repoRoot = GetRepositoryRoot();
        AssertFileDoesNotContain(Path.Combine(repoRoot, "Server", "Connections.Server", "NetServer.cs"), "Abort(");
    }
    private sealed class DelegateServerInboundMessageInterceptor : IServerInboundMessageInterceptor
    {
        private readonly Func<FrameworkPacket, bool> canIntercept;
        private readonly Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingMessageResult> handle;

        public DelegateServerInboundMessageInterceptor(Func<FrameworkPacket, bool> canIntercept, Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingMessageResult> handle)
        {
            this.canIntercept = canIntercept;
            this.handle = handle;
        }

        public int Priority => 0;

        public bool CanInterceptIncomingMessage(FrameworkPacket message) => canIntercept(message);

        public ServerIncomingMessageResult InterceptIncomingMessage(FrameworkPacket message, ServerFrameworkContext context) => handle(message, context);
    }

    private sealed class DelegateServerDefaultMessageHandler : IServerDefaultMessageHandler
    {
        private readonly Func<FrameworkPacket, bool> canHandle;
        private readonly Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingMessageResult> handle;

        public DelegateServerDefaultMessageHandler(Func<FrameworkPacket, bool> canHandle, Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingMessageResult> handle)
        {
            this.canHandle = canHandle;
            this.handle = handle;
        }

        public int Priority => 0;

        public bool CanHandleIncomingMessage(FrameworkPacket message) => canHandle(message);

        public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ServerFrameworkContext context) => handle(message, context);
    }

    private sealed class DelegateServerInboundCommandInterceptor : IServerInboundCommandInterceptor
    {
        private readonly Func<FrameworkPacket, bool> canIntercept;
        private readonly Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingCommandResult> handle;

        public DelegateServerInboundCommandInterceptor(Func<FrameworkPacket, bool> canIntercept, Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingCommandResult> handle)
        {
            this.canIntercept = canIntercept;
            this.handle = handle;
        }

        public int Priority => 0;

        public bool CanInterceptIncomingCommand(FrameworkPacket command) => canIntercept(command);

        public ServerIncomingCommandResult InterceptIncomingCommand(FrameworkPacket command, ServerFrameworkContext context) => handle(command, context);
    }

    private sealed class DelegateServerDefaultCommandHandler : IServerDefaultCommandHandler
    {
        private readonly Func<FrameworkPacket, bool> canHandle;
        private readonly Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingCommandResult> handle;

        public DelegateServerDefaultCommandHandler(Func<FrameworkPacket, bool> canHandle, Func<FrameworkPacket, ServerFrameworkContext, ServerIncomingCommandResult> handle)
        {
            this.canHandle = canHandle;
            this.handle = handle;
        }

        public int Priority => 0;

        public bool CanHandleIncomingCommand(FrameworkPacket command) => canHandle(command);

        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context) => handle(command, context);
    }

    private sealed class DelegateServerOutboundPacketInterceptor : IServerOutboundPacketInterceptor
    {
        private readonly Func<FrameworkPacket, bool> canIntercept;
        private readonly Func<FrameworkPacket, ServerOutboundPacketContext, ServerOutgoingPacketResult> handle;

        public DelegateServerOutboundPacketInterceptor(Func<FrameworkPacket, bool> canIntercept, Func<FrameworkPacket, ServerOutboundPacketContext, ServerOutgoingPacketResult> handle)
        {
            this.canIntercept = canIntercept;
            this.handle = handle;
        }

        public int Priority => 0;

        public bool CanInterceptOutgoingPacket(FrameworkPacket packet, ServerOutboundPacketContext context) => canIntercept(packet);

        public ServerOutgoingPacketResult InterceptOutgoingPacket(FrameworkPacket packet, ServerOutboundPacketContext context) => handle(packet, context);
    }

    private sealed class DelegateServerMessageObserver : IServerMessageObserver
    {
        private readonly Func<FrameworkPacket, bool> canObserve;
        private readonly Action<FrameworkPacket, ServerFrameworkContext, MessageHandlingResultAction> observe;

        public DelegateServerMessageObserver(
            Func<FrameworkPacket, bool> canObserve,
            Action<FrameworkPacket, ServerFrameworkContext, MessageHandlingResultAction> observe)
        {
            this.canObserve = canObserve;
            this.observe = observe;
        }

        public int Priority => 0;

        public bool CanObserveIncomingMessage(FrameworkPacket message) => canObserve(message);

        public void ObserveIncomingMessage(FrameworkPacket message, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
            => observe(message, context, terminalAction);
    }

    private sealed class DelegateServerCommandObserver : IServerCommandObserver
    {
        private readonly Func<FrameworkPacket, bool> canObserve;
        private readonly Action<FrameworkPacket, ServerFrameworkContext, MessageHandlingResultAction> observe;

        public DelegateServerCommandObserver(
            Func<FrameworkPacket, bool> canObserve,
            Action<FrameworkPacket, ServerFrameworkContext, MessageHandlingResultAction> observe)
        {
            this.canObserve = canObserve;
            this.observe = observe;
        }

        public int Priority => 0;

        public bool CanObserveIncomingCommand(FrameworkPacket command) => canObserve(command);

        public void ObserveIncomingCommand(FrameworkPacket command, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
            => observe(command, context, terminalAction);
    }

    private sealed class PipelineTestHarness
    {
        public readonly List<IServerInboundMessageInterceptor> InboundMessageInterceptors = new List<IServerInboundMessageInterceptor>();
        public readonly List<IServerDefaultMessageHandler> DefaultMessageHandlers = new List<IServerDefaultMessageHandler>();
        public readonly List<IServerMessageObserver> MessageObservers = new List<IServerMessageObserver>();
        public readonly List<IServerInboundCommandInterceptor> InboundCommandInterceptors = new List<IServerInboundCommandInterceptor>();
        public readonly List<IServerDefaultCommandHandler> DefaultCommandHandlers = new List<IServerDefaultCommandHandler>();
        public readonly List<IServerCommandObserver> CommandObservers = new List<IServerCommandObserver>();
        public readonly List<IServerOutboundPacketInterceptor> OutboundPacketInterceptors = new List<IServerOutboundPacketInterceptor>();
        public readonly List<IItemCodec> ItemCodecs = new List<IItemCodec>();

        public PipelineTestHarness()
        {
            Runner = new ServerPipelineRunner(
                InboundMessageInterceptors,
                DefaultMessageHandlers,
                MessageObservers,
                InboundCommandInterceptors,
                DefaultCommandHandlers,
                CommandObservers,
                OutboundPacketInterceptors,
                ItemCodecs);
        }

        public ServerPipelineRunner Runner { get; }
    }

    private static ServerFrameworkContext CreateServerContext()
    {
        return new ServerFrameworkContext
        {
            ConnectionId = "connection-1",
            SessionId = "session-1",
            SenderUuid = "sender-1",
            RemoteCapabilities = Array.Empty<string>(),
            ServerCapabilities = Array.Empty<string>(),
            HasRemoteCapability = _ => false,
            ConnectionHasCapability = (_, __) => false,
            SendMessage = (_, __) => { },
            BroadcastMessage = (_, __) => { },
            Log = (_, __) => { }
        };
    }    private static void AssertFileDoesNotContain(string path, string forbiddenText)
    {
        string content = File.ReadAllText(path);
        Assert(!content.Contains(forbiddenText), $"Expected '{path}' to stop using '{forbiddenText}'.");
    }
    private static string GetRepositoryRoot()
    {
        for (DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Server", "Server.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Phinix.sln above the test output directory.");
    }
    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
