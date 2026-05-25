using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using UserManagement;
using Utils;
using Utils.Framework;

namespace PhinixServer.Framework
{
    public interface IFrameworkChatServerApi : ILoggable, IPersistent
    {
        global::Phinix.Framework.BuiltInChatMessagePayload AddMessage(string senderUuid, string message);

        global::Phinix.Framework.BuiltInChatMessagePayload[] GetHistory();

        FrameworkPacket BuildBroadcastPacket(global::Phinix.Framework.BuiltInChatMessagePayload chatMessage);

        FrameworkPacket BuildHistorySyncCompletePacket();
    }

    public class PhinixFrameworkChatService : IFrameworkChatServerApi
    {
        private readonly ServerUserManager userManager;
        public event EventHandler<LogEventArgs> OnLogEntry;

        public void RaiseLogEntry(LogEventArgs e) => OnLogEntry?.Invoke(this, e);

        private readonly List<global::Phinix.Framework.BuiltInChatMessagePayload> messageHistory = new List<global::Phinix.Framework.BuiltInChatMessagePayload>();
        private readonly object messageHistoryLock = new object();
        private readonly int messageHistoryCapacity;
        private readonly PhinixFrameworkChatBroadcast broadcastBuilder = new PhinixFrameworkChatBroadcast();

        public PhinixFrameworkChatService(int messageHistoryCapacity, ServerUserManager userManager)
        {
            this.messageHistoryCapacity = messageHistoryCapacity;
            this.userManager = userManager;
        }

        public global::Phinix.Framework.BuiltInChatMessagePayload AddMessage(string senderUuid, string message)
        {
            global::Phinix.Framework.BuiltInChatMessagePayload chatMessage = new global::Phinix.Framework.BuiltInChatMessagePayload
            {
                MessageId = Guid.NewGuid().ToString(),
                SenderUuid = senderUuid ?? string.Empty,
                Message = TextHelper.SanitiseRichText(message ?? string.Empty),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            lock (messageHistoryLock)
            {
                messageHistory.Add(chatMessage.Clone());
                if (messageHistory.Count > messageHistoryCapacity)
                {
                    messageHistory.RemoveAt(0);
                }
            }

            if (userManager == null || !userManager.TryGetDisplayName(chatMessage.SenderUuid, out string displayName))
            {
                displayName = "??? (" + chatMessage.SenderUuid + ")";
            }

            RaiseLogEntry(new LogEventArgs($"{TextHelper.StripRichText(displayName)}: {chatMessage.Message}"));
            return chatMessage;
        }

        public global::Phinix.Framework.BuiltInChatMessagePayload[] GetHistory()
        {
            lock (messageHistoryLock)
            {
                return messageHistory
                    .Select(message => message.Clone())
                    .ToArray();
            }
        }

        public FrameworkPacket BuildBroadcastPacket(global::Phinix.Framework.BuiltInChatMessagePayload chatMessage)
        {
            return broadcastBuilder.BuildChatMessage(chatMessage);
        }

        public FrameworkPacket BuildHistorySyncCompletePacket()
        {
            return broadcastBuilder.BuildHistorySyncComplete();
        }

        public void Save(string path)
        {
            global::Phinix.Framework.BuiltInChatHistoryStore store;
            lock (messageHistoryLock)
            {
                store = new global::Phinix.Framework.BuiltInChatHistoryStore
                {
                    ChatMessages = { messageHistory.Select(message => message.Clone()) }
                };
            }

            FileStream fs = File.Exists(path)
                ? File.Open(path, FileMode.Truncate, FileAccess.Write)
                : File.Create(path);
            using (fs)
            using (CodedOutputStream cos = new CodedOutputStream(fs))
            {
                store.WriteTo(cos);
            }

            RaiseLogEntry(new LogEventArgs($"Saved {store.ChatMessages.Count} framework chat message{(store.ChatMessages.Count != 1 ? "s" : "")}"));
        }

        public void Load(string path)
        {
            lock (messageHistoryLock)
            {
                if (!File.Exists(path))
                {
                    RaiseLogEntry(new LogEventArgs("No framework chat history file, generating a new one"));
                    messageHistory.Clear();
                    Save(path);
                    return;
                }

                global::Phinix.Framework.BuiltInChatHistoryStore store;
                using (FileStream fs = new FileStream(path, FileMode.Open))
                using (CodedInputStream cis = new CodedInputStream(fs))
                {
                    store = global::Phinix.Framework.BuiltInChatHistoryStore.Parser.ParseFrom(cis);
                }

                messageHistory.Clear();
                messageHistory.AddRange(store.ChatMessages.Select(message => message.Clone()));

                RaiseLogEntry(new LogEventArgs($"Loaded {messageHistory.Count} framework chat message{(messageHistory.Count != 1 ? "s" : "")}"));
            }
        }
    }
}
