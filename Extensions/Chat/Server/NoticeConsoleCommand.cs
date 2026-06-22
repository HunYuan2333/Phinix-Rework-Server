using System;
using System.Collections.Generic;
using System.Linq;
using Utils;
using Utils.Framework;

namespace Phinix.ChatExtension.Server
{
    internal sealed class NoticeConsoleCommand : IServerConsoleCommandProvider
    {
        private readonly IFrameworkChatServerApi chatApi;
        private readonly Func<IFrameworkServerBroadcaster> broadcasterResolver;

        public string CommandName => "notice";

        public NoticeConsoleCommand(
            IFrameworkChatServerApi chatApi,
            Func<IFrameworkServerBroadcaster> broadcasterResolver)
        {
            this.chatApi = chatApi;
            this.broadcasterResolver = broadcasterResolver;
        }

        public bool Execute(List<string> args)
        {
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: notice [durationSeconds] <text>");
                return false;
            }

            int durationSeconds = 0;
            string text;
            int textStartIndex = 0;

            if (args.Count >= 2 && int.TryParse(args[0], out durationSeconds) && durationSeconds > 0)
            {
                textStartIndex = 1;
            }

            text = string.Join(" ", args.Skip(textStartIndex));
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine("Notice text cannot be empty.");
                return false;
            }

            IFrameworkServerBroadcaster broadcaster = broadcasterResolver?.Invoke();
            if (broadcaster == null)
            {
                Console.WriteLine("Broadcast service not available.");
                return false;
            }

            global::Phinix.Framework.BuiltInChatMessagePayload notice = chatApi.AddNotice(text, durationSeconds);
            FrameworkPacket packet = chatApi.BuildBroadcastPacket(notice);

            broadcaster.Broadcast("builtin.chat", packet);
            Console.WriteLine("Notice broadcast: " + TextHelper.StripRichText(text));

            return true;
        }
    }
}
