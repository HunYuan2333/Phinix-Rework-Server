using System.Runtime.Serialization;
using Utils.Framework;

namespace Phinix.TradeExtension.Server
{
    [DataContract]
    public sealed class TradeServerConfig : IExtensionConfigSection
    {
        [DataMember(Order = 0)]
        public string DatabasePath { get; set; }

        public string SectionName => "builtin.trade";

        public void LoadDefaults()
        {
            DatabasePath = "trades";
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(DatabasePath)) DatabasePath = "trades";
        }
    }
}
