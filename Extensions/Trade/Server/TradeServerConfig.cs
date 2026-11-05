using System;
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

        [DataMember(Order = 1)] public int PrincipalRetainedTradeLimit { get; set; } = 32;
        [DataMember(Order = 2)] public int PrincipalPayloadByteLimit { get; set; } = 1024 * 1024;
        [DataMember(Order = 3)] public int PrincipalAuxiliaryByteLimit { get; set; } = 64 * 1024;
        [DataMember(Order = 4)] public int RecoveryPageSize { get; set; } = 8;

        [DataMember(Order=5)] public int StorageMiBLimit {get;set;}=256;
        // Opt-in verbose tracing; failures and persistence diagnostics remain unconditional.
        [DataMember(Order=6)] public bool DiagnosticLogging {get;set;}
        public string SectionName => "builtin.trade";

        public void LoadDefaults()
        {
            DatabasePath = "trades";
            PrincipalRetainedTradeLimit = 32;
            PrincipalPayloadByteLimit = 1024 * 1024;
            PrincipalAuxiliaryByteLimit = 64 * 1024;
            RecoveryPageSize = 8;StorageMiBLimit=256;DiagnosticLogging=false;
        }

        // DataContractSerializer bypasses constructors/property initializers.
        [OnDeserializing] private void BeforeRead(StreamingContext _) => LoadDefaults();

        public void Validate()
        {
            if (string.IsNullOrEmpty(DatabasePath)) DatabasePath = "trades";
            if (PrincipalRetainedTradeLimit < 0 || PrincipalPayloadByteLimit < 0 || PrincipalAuxiliaryByteLimit < 0 ||
                RecoveryPageSize < 1 || RecoveryPageSize > 64 || (StorageMiBLimit!=0&&(StorageMiBLimit<32||StorageMiBLimit>16384)))
                throw new ArgumentOutOfRangeException(nameof(TradeServerConfig), "Invalid trade limits; quota 0 disables that quota, page size must be 1..64.");
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
