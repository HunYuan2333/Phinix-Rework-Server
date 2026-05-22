using System.Collections.Generic;
using Utils;
using Utils.Framework;

namespace PhinixServer.Extensions
{
    [PhinixExtension("sample.red_packet")]
    public class RedPacketServerExtension : IPhinixExtension, ICapabilityProvider, IServerMessageHandler
    {
        public string ExtensionId => "sample.red_packet";

        public int Priority => 100;

        public IEnumerable<string> GetCapabilities()
        {
            yield return "sample.red_packet";
        }

        public bool CanHandleIncomingEnvelope(FrameworkEnvelope envelope)
        {
            return envelope != null && envelope.MessageType == ExtensionId;
        }

        public ServerIncomingMessageResult HandleIncomingEnvelope(FrameworkEnvelope envelope, ServerFrameworkContext context)
        {
            context.Log?.Invoke($"Broadcasting red packet message from {context.SenderUuid.Highlight(HighlightType.UUID)}", LogLevel.DEBUG);
            context.BroadcastEnvelope?.Invoke(envelope, null);

            return new ServerIncomingMessageResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }
    }
}
