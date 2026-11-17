using Utils.Framework;
namespace ServerRuntime
{
    public sealed class CommandProcessingResult
    {
        public bool Consumed { get; set; }
        public MessageHandlingResultAction Action { get; set; }
        public FrameworkPacket Command { get; set; }
        public RelayResult Relay { get; set; }
    }
}
