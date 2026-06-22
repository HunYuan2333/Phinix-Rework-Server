using System.Collections.Generic;
using System.Runtime.Serialization;
using Utils.Framework;

namespace Phinix.ChatExtension.Server
{
    [DataContract]
    public sealed class ChatServerConfig : IExtensionConfigSection, ILegacyExtensionConfigMigrator
    {
        [DataMember(Order = 0)]
        public string HistoryPath { get; set; }

        [DataMember(Order = 1)]
        public int HistoryLength { get; set; }

        [DataMember(Order = 2)]
        public int NoticeHistoryLength { get; set; }

        [DataMember(Order = 3)]
        public int NoticeRetentionWindowHours { get; set; }

        public string SectionName => "builtin.chat";

        public void LoadDefaults()
        {
            HistoryPath = "chatHistory";
            HistoryLength = 40;
            NoticeHistoryLength = 50;
            NoticeRetentionWindowHours = 24;
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(HistoryPath)) HistoryPath = "chatHistory";
            if (HistoryLength < 0) HistoryLength = 40;
            if (NoticeHistoryLength < 0) NoticeHistoryLength = 50;
            if (NoticeRetentionWindowHours < 1) NoticeRetentionWindowHours = 24;
        }

        public bool TryMigrateLegacyConfig(IReadOnlyDictionary<string, string> legacyValues)
        {
            if (legacyValues == null)
            {
                return false;
            }

            bool migrated = false;
            if (legacyValues.TryGetValue("server.legacy.chatHistoryPath", out string historyPath) &&
                !string.IsNullOrWhiteSpace(historyPath))
            {
                HistoryPath = historyPath;
                migrated = true;
            }

            if (legacyValues.TryGetValue("server.legacy.chatHistoryLength", out string historyLengthRaw) &&
                int.TryParse(historyLengthRaw, out int historyLength) &&
                historyLength > 0)
            {
                HistoryLength = historyLength;
                migrated = true;
            }

            return migrated;
        }
    }
}
