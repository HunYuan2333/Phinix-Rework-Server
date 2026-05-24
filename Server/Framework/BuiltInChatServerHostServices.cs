namespace PhinixServer.Framework
{
    public sealed class BuiltInChatServerHostServices
    {
        public BuiltInChatServerHostServices(PhinixFrameworkChatService chatService)
        {
            ChatService = chatService;
        }

        public PhinixFrameworkChatService ChatService { get; }
    }
}
