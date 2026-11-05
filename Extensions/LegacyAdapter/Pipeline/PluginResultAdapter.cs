using Utils;
using Utils.Framework;

namespace Phinix.ExtensionCompatibility
{
    // Invocation boundary for the frozen plugin ABI, not a wire/profile selector.
    // Compiled into the host composition assembly; ServerRuntime has no dependency on this adapter.
    internal static class PluginResultAdapter
    {
        private static void Report(ServerFrameworkContext context)
        {
            // A logger failure cannot turn a completed plugin call into a business retry.
            try { context?.Log?.Invoke("Obsolete plugin result normalized to Continue at extension boundary.", LogLevel.WARNING); }
            catch { }
        }
        public static ServerIncomingMessageResult Normalize(ServerIncomingMessageResult result, ServerFrameworkContext context)
        {
            if (result?.Action != MessageHandlingResultAction.LegacyFallback) return result;
            Report(context);
            return new ServerIncomingMessageResult { Action = MessageHandlingResultAction.Continue, Message = result.Message };
        }
        public static ServerIncomingCommandResult Normalize(ServerIncomingCommandResult result, ServerFrameworkContext context)
        {
            if (result?.Action != MessageHandlingResultAction.LegacyFallback) return result;
            Report(context);
            return new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Continue, Command = result.Command };
        }
        public static ServerIncomingItemResult Normalize(ServerIncomingItemResult result, ServerFrameworkContext context)
        {
            if (result?.Action != ItemHandlingResultAction.LegacyFallback) return result;
            Report(context);
            return new ServerIncomingItemResult {
                Action = ItemHandlingResultAction.Continue, Item = result.Item,
                ReplacedPayload = result.ReplacedPayload, DecodedItem = result.DecodedItem,
                HandledByHandlerId = result.HandledByHandlerId,
                FailureReason = result.FailureReason, FailureException = result.FailureException
            };
        }
    }
}
