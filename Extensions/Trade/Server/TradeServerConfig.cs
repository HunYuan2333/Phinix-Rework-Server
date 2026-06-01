using System.Collections.Generic;
using System.Runtime.Serialization;
using Utils.Framework;

namespace Phinix.TradeExtension.Server
{
    [DataContract]
    public sealed class TradeServerConfig : IExtensionConfigSection, ILegacyExtensionConfigMigrator
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

        public bool TryMigrateLegacyConfig(IReadOnlyDictionary<string, string> legacyValues)
        {
            if (legacyValues == null)
            {
                return false;
            }

            if (legacyValues.TryGetValue("server.legacy.tradeDatabasePath", out string databasePath) &&
                !string.IsNullOrWhiteSpace(databasePath))
            {
                DatabasePath = databasePath;
                return true;
            }

            return false;
        }
    }
}
