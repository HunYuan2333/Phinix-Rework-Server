using System.Collections.Generic;
using Utils;
using Utils.Framework;

namespace PhinixServer.Extensions
{
    [PhinixExtension("sample.red_packet")]
    public sealed class RedPacketServerExtension : IPhinixExtensionModule, ICapabilityProvider, IServerMessageHandler
    {
        public string ExtensionId => "sample.red_packet";

        public int Priority => 100;

        public void Register(IExtensionBuilder builder)
        {
            builder.AddCapabilityProvider(this);
            builder.AddServerMessageHandler(this);
        }

        public IEnumerable<string> GetCapabilities()
        {
            yield return "sample.red_packet";
        }

        public bool CanHandleIncomingMessage(FrameworkPacket message)
        {
            return message != null && message.MessageType == ExtensionId;
        }

        public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ServerFrameworkContext context)
        {
            context.Log?.Invoke($"Broadcasting red packet message from {context.SenderUuid.Highlight(HighlightType.UUID)}", LogLevel.DEBUG);
            context.BroadcastMessage?.Invoke(message, null);

            return new ServerIncomingMessageResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }
    }
}
