using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using UserManagement;
using Utils;
using Utils.Framework;

namespace Phinix.ChatExtension.Server
{
    public interface IFrameworkChatServerApi : ILoggable, IPersistent
    {
        global::Phinix.Framework.BuiltInChatMessagePayload AddMessage(string senderUuid, string message);

        global::Phinix.Framework.BuiltInChatMessagePayload AddMessage(
            string senderUuid,
            string message,
            IEnumerable<string> mentionedUuids,
            string replyToMessageId,
            string replyToSnippet);

        global::Phinix.Framework.BuiltInChatMessagePayload[] GetHistory();

        FrameworkPacket BuildBroadcastPacket(global::Phinix.Framework.BuiltInChatMessagePayload chatMessage);

        FrameworkPacket BuildHistorySyncCompletePacket();

        global::Phinix.Framework.BuiltInChatMessagePayload BuildNotice(string text, int durationSeconds);

        global::Phinix.Framework.BuiltInChatMessagePayload AddNotice(string text, int durationSeconds);

        global::Phinix.Framework.BuiltInChatMessagePayload[] GetNotices();

        global::Phinix.Framework.BuiltInChatMessagePayload[] GetNoticesSince(DateTime cutoffUtc);

        int NoticeRetentionWindowHours { get; }
    }

    public class PhinixFrameworkChatService : IFrameworkChatServerApi
    {
        private readonly IServerUserManager userManager;
        public event EventHandler<LogEventArgs> OnLogEntry;

        public void RaiseLogEntry(LogEventArgs e) => OnLogEntry?.Invoke(this, e);

        private readonly List<global::Phinix.Framework.BuiltInChatMessagePayload> messageHistory = new List<global::Phinix.Framework.BuiltInChatMessagePayload>();
        private readonly object messageHistoryLock = new object();
        private readonly int messageHistoryCapacity;
        private readonly List<global::Phinix.Framework.BuiltInChatMessagePayload> noticeHistory = new List<global::Phinix.Framework.BuiltInChatMessagePayload>();
        private readonly object noticeHistoryLock = new object();
        private readonly int noticeHistoryCapacity;
        private readonly int noticeRetentionWindowHours;
        private readonly PhinixFrameworkChatBroadcast broadcastBuilder = new PhinixFrameworkChatBroadcast();

        public int NoticeRetentionWindowHours => noticeRetentionWindowHours;

        public PhinixFrameworkChatService(int messageHistoryCapacity, int noticeHistoryCapacity, int noticeRetentionWindowHours, IServerUserManager userManager)
        {
            this.messageHistoryCapacity = messageHistoryCapacity;
            this.noticeHistoryCapacity = noticeHistoryCapacity;
            this.noticeRetentionWindowHours = noticeRetentionWindowHours;
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

        public global::Phinix.Framework.BuiltInChatMessagePayload AddMessage(
            string senderUuid,
            string message,
            IEnumerable<string> mentionedUuids,
            string replyToMessageId,
            string replyToSnippet)
        {
            global::Phinix.Framework.BuiltInChatMessagePayload chatMessage = new global::Phinix.Framework.BuiltInChatMessagePayload
            {
                MessageId = Guid.NewGuid().ToString(),
                SenderUuid = senderUuid ?? string.Empty,
                Message = TextHelper.SanitiseRichText(message ?? string.Empty),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            if (mentionedUuids != null)
            {
                chatMessage.MentionedUuids.AddRange(mentionedUuids);
            }

            if (!string.IsNullOrEmpty(replyToMessageId))
            {
                chatMessage.ReplyToMessageId = replyToMessageId;
            }

            if (!string.IsNullOrEmpty(replyToSnippet))
            {
                chatMessage.ReplyToSnippet = replyToSnippet;
            }

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

        public global::Phinix.Framework.BuiltInChatMessagePayload BuildNotice(string text, int durationSeconds)
        {
            return new global::Phinix.Framework.BuiltInChatMessagePayload
            {
                MessageId = Guid.NewGuid().ToString(),
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                Message = TextHelper.SanitiseRichText(text ?? string.Empty),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                IsNotice = true,
                NoticeDurationSeconds = durationSeconds
            };
        }

        public global::Phinix.Framework.BuiltInChatMessagePayload AddNotice(string text, int durationSeconds)
        {
            var notice = new global::Phinix.Framework.BuiltInChatMessagePayload
            {
                MessageId = Guid.NewGuid().ToString(),
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                Message = TextHelper.SanitiseRichText(text ?? string.Empty),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                IsNotice = true,
                NoticeDurationSeconds = durationSeconds
            };

            lock (noticeHistoryLock)
            {
                noticeHistory.Add(notice.Clone());
                if (noticeHistory.Count > noticeHistoryCapacity)
                    noticeHistory.RemoveAt(0);
            }

            RaiseLogEntry(new LogEventArgs($"Notice added: {TextHelper.StripRichText(text)}"));
            return notice;
        }

        public global::Phinix.Framework.BuiltInChatMessagePayload[] GetNotices()
        {
            lock (noticeHistoryLock)
            {
                return noticeHistory.Select(n => n.Clone()).ToArray();
            }
        }

        public global::Phinix.Framework.BuiltInChatMessagePayload[] GetNoticesSince(DateTime cutoffUtc)
        {
            lock (noticeHistoryLock)
            {
                return noticeHistory
                    .Where(n => n.Timestamp != null && n.Timestamp.ToDateTime().ToUniversalTime() >= cutoffUtc)
                    .Select(n => n.Clone())
                    .ToArray();
            }
        }

        public void Save(string path)
        {
            global::Phinix.Framework.BuiltInChatHistoryStore store;
            lock (messageHistoryLock)
            lock (noticeHistoryLock)
            {
                store = new global::Phinix.Framework.BuiltInChatHistoryStore
                {
                    ChatMessages = { messageHistory.Select(message => message.Clone()) },
                    Notices = { noticeHistory.Select(notice => notice.Clone()) }
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

            RaiseLogEntry(new LogEventArgs($"Saved {store.ChatMessages.Count} chat messages, {store.Notices.Count} notices"));
        }

        public void Load(string path)
        {
            lock (messageHistoryLock)
            lock (noticeHistoryLock)
            {
                if (!File.Exists(path))
                {
                    RaiseLogEntry(new LogEventArgs("No framework chat history file, generating a new one"));
                    messageHistory.Clear();
                    noticeHistory.Clear();
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

                noticeHistory.Clear();
                noticeHistory.AddRange(store.Notices.Select(notice => notice.Clone()));

                RaiseLogEntry(new LogEventArgs($"Loaded {messageHistory.Count} chat messages, {noticeHistory.Count} notices"));
            }
        }
    }
}
