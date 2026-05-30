using System.Runtime.Serialization;
using Utils.Framework;

namespace Phinix.ChatExtension.Server
{
    [DataContract]
    public sealed class ChatServerConfig : IExtensionConfigSection
    {
        [DataMember(Order = 0)]
        public string HistoryPath { get; set; }

        [DataMember(Order = 1)]
        public int HistoryLength { get; set; }

        public string SectionName => "builtin.chat";

        public void LoadDefaults()
        {
            HistoryPath = "chatHistory";
            HistoryLength = 40;
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(HistoryPath)) HistoryPath = "chatHistory";
            if (HistoryLength < 0) HistoryLength = 40;
        }
    }
}
