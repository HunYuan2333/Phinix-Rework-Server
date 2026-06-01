using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Xml;
using Authentication;
using Utils.Framework;

namespace PhinixServer
{
    /// <inheritdoc />
    /// <summary>
    /// Server configuration class able to read from and write to a configuration file.
    /// </summary>
    [DataContract]
    public class Config : IExtensionConfigProvider
    {
        /// <summary>
        /// IP address to listen on.
        /// </summary>
        [IgnoreDataMember]
        public IPAddress Address = IPAddress.Any;

        /// <summary>
        /// Textual representation of Address.
        /// This is reserved for (de)serialisation purposes.
        /// </summary>
        [DataMember(Name = "IPAddress", Order = 0)]
        private string addressString = "";

        /// <summary>
        /// Port to listen on.
        /// </summary>
        [DataMember(Name = "Port", Order = 1)]
        public int Port = 16200;

        /// <summary>
        /// Maximum number of connections to accept at once.
        /// </summary>
        [DataMember(Name = "MaxConnections", Order = 2)]
        public int MaxConnections = 1000;

        /// <summary>
        /// Path to the log file.
        /// </summary>
        [DataMember(Name = "LogFile", Order = 3)]
        public string LogPath = "server.log";

        /// <summary>
        /// Save interval in milliseconds.
        /// </summary>
        [DataMember(Name = "SaveInterval", Order = 4)]
        public int SaveInterval = 60000;

        /// <summary>
        /// The minimum verbosity level for a message to be displayed in the console.
        /// </summary>
        [DataMember(Name = "DisplayVerbosity", Order = 5)]
        public Verbosity DisplayVerbosity = Verbosity.INFO;

        /// <summary>
        /// The minimum verbosity level for a message to be recorded in the log file.
        /// </summary>
        [DataMember(Name = "LogVerbosity", Order = 6)]
        public Verbosity LogVerbosity = Verbosity.INFO;

        /// <summary>
        /// Path to the user database file.
        /// </summary>
        [DataMember(Name = "UserDatabaseFile", Order = 7)]
        public string UserDatabasePath = "users";

        /// <summary>
        /// Path to the credential database file.
        /// </summary>
        [DataMember(Name = "CredentialDatabaseFile", Order = 8)]
        public string CredentialDatabasePath = "credentials";

        /// <summary>
        /// Name of the server as shown to clients.
        /// </summary>
        [DataMember(Name = "ServerName", Order = 9)]
        public string ServerName = "Phinix Server";

        /// <summary>
        /// Description of the server as shown to clients.
        /// </summary>
        [DataMember(Name = "ServerDescription", Order = 10)]
        public string ServerDescription = "A Phinix server.";

        /// <summary>
        /// Authentication type clients must use when connecting.
        /// </summary>
        [DataMember(Name = "AuthType", Order = 11)]
        public AuthTypes AuthType = AuthTypes.ClientKey;

        /// <summary>
        /// Maximum display name length for users.
        /// </summary>
        [DataMember(Name = "MaxDisplayNameLength", Order = 12)]
        public int MaxDisplayNameLength = 100;

        /// <summary>
        /// Extension configuration stored as key-value pairs.
        /// </summary>
        [DataMember(Name = "ExtensionConfigs", Order = 13)]
        public Dictionary<string, string> ExtensionConfigs = new Dictionary<string, string>();

        // Legacy fields — only present to catch old XML elements during deserialization.
        // Moved to ExtensionConfigs in OnDeserialized, then cleared so they never re-appear on Save.

        [DataMember(Name = "ChatHistoryFile", Order = 14, EmitDefaultValue = false)]
        private string legacyChatHistoryPath;

        [DataMember(Name = "TradeDatabaseFile", Order = 15, EmitDefaultValue = false)]
        private string legacyTradeDatabasePath;

        [DataMember(Name = "ChatHistoryLength", Order = 16)]
        private int legacyChatHistoryLength;

        /// <summary>
        /// True after a legacy extension config migration succeeds, so Load() can trigger a re-save.
        /// </summary>
        [IgnoreDataMember]
        private bool migrationPerformed;

        /// <summary>
        /// Loads a <see cref="Config"/> object from the given file path. Will return a default <see cref="Config"/> if the file does not exist.
        /// </summary>
        /// <param name="filePath">Config file path</param>
        /// <returns>Loaded <see cref="Config"/> object</returns>
        public static Config Load(string filePath)
        {
            // Give a fresh new config if the given file doesn't exist
            if (!File.Exists(filePath))
            {
                Config config = new Config();
                config.Save(filePath); // Save it first to make sure it's present next time
                return config;
            }

            Config result;

            using (XmlReader reader = XmlReader.Create(filePath))
            {
                result = new DataContractSerializer(typeof(Config)).ReadObject(reader) as Config;
            }

            // Persist immediately when a legacy extension config migration succeeds, so old XML nodes never re-appear.
            if (result != null && result.migrationPerformed)
            {
                result.migrationPerformed = false;
                result.Save(filePath);
            }

            return result;
        }

        /// <summary>
        /// Saves the <see cref="Config"/> object to an XML document at the given path.
        /// This will overwrite the file if it already exists.
        /// </summary>
        /// <param name="filePath">Destination file path</param>
        public void Save(string filePath)
        {
            XmlWriterSettings settings = new XmlWriterSettings { Indent = true };
            using (XmlWriter writer = XmlWriter.Create(filePath, settings))
            {
                new DataContractSerializer(typeof(Config)).WriteObject(writer, this);
            }
        }

        /// <summary>
        /// Called before the <see cref="Config"/> is serialised.
        /// Used to convert complex types to something easier to edit by hand.
        /// </summary>
        /// <param name="context"></param>
        [OnSerializing]
        private void OnSerializing(StreamingContext context)
        {
            this.addressString = Address.ToString();
        }

        [OnDeserializing]
        private void OnDeserialising(StreamingContext context)
        {
            if (string.IsNullOrEmpty(addressString)) addressString = IPAddress.Any.ToString();
            if (Port < 1 || Port > 65535) Port = 16200;
            if (MaxConnections < 0) MaxConnections = 1000;
            if (string.IsNullOrEmpty(LogPath)) LogPath = "server.log";
            if (SaveInterval < 1) SaveInterval = 60000;
            // Ignore Display- and LogVerbosity since they always have a value
            if (string.IsNullOrEmpty(UserDatabasePath)) UserDatabasePath = "users";
            if (string.IsNullOrEmpty(CredentialDatabasePath)) CredentialDatabasePath = "credentials";
            if (string.IsNullOrEmpty(ServerName)) ServerName = "Phinix Server";
            if (string.IsNullOrEmpty(ServerDescription)) ServerDescription = "A Phinix server.";
            // Ignore AuthType since it always has a value
            if (MaxDisplayNameLength < 1) MaxDisplayNameLength = 100;
            if (ExtensionConfigs == null) ExtensionConfigs = new Dictionary<string, string>();
        }

        /// <summary>
        /// Called after the <see cref="Config"/> is deserialised.
        /// Used to convert easy-to-edit types back into their complex counterparts.
        /// </summary>
        /// <param name="context"></param>
        [OnDeserialized]
        private void OnDeserialised(StreamingContext context)
        {
            if (!IPAddress.TryParse(addressString, out Address))
            {
                throw new ConfigItemDeserialisationException(typeof(string), typeof(IPAddress), nameof(addressString));
            }

            if (ExtensionConfigs == null)
            {
                ExtensionConfigs = new Dictionary<string, string>();
            }
        }

        /// <inheritdoc cref="IExtensionConfigProvider.GetConfig{T}"/>
        public T GetConfig<T>() where T : IExtensionConfigSection, new()
        {
            T section = new T();
            section.LoadDefaults();
            bool hasPersistedConfig = false;

            // Apply overrides from ExtensionConfigs dictionary
            if (ExtensionConfigs != null && ExtensionConfigs.TryGetValue(section.SectionName, out string json))
            {
                hasPersistedConfig = true;
                try
                {
                    using (System.IO.MemoryStream ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                    {
                        DataContractSerializer serializer = new DataContractSerializer(typeof(T));
                        T overrides = (T)serializer.ReadObject(ms);
                        // Shallow merge via reflection — overwrite non-default properties
                        foreach (var prop in typeof(T).GetProperties())
                        {
                            if (prop.CanWrite)
                            {
                                object value = prop.GetValue(overrides);
                                if (value != null)
                                {
                                    prop.SetValue(section, value);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // If deserialization fails, use defaults
                }
            }

            if (!hasPersistedConfig && section is ILegacyExtensionConfigMigrator migrator)
            {
                if (migrator.TryMigrateLegacyConfig(BuildLegacyConfigValues()))
                {
                    migrationPerformed = true;
                    SaveConfig(section);
                    clearLegacyConfigFields();
                }
            }

            section.Validate();

            return section;
        }

        /// <inheritdoc cref="IExtensionConfigProvider.SaveConfig{T}"/>
        public void SaveConfig<T>(T config) where T : IExtensionConfigSection
        {
            if (ExtensionConfigs == null)
            {
                ExtensionConfigs = new Dictionary<string, string>();
            }

            DataContractSerializer serializer = new DataContractSerializer(typeof(T));
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                using (XmlDictionaryWriter writer = XmlDictionaryWriter.CreateTextWriter(ms, System.Text.Encoding.UTF8, ownsStream: false))
                {
                    serializer.WriteObject(writer, config);
                    writer.Flush();
                    ExtensionConfigs[config.SectionName] = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        private IReadOnlyDictionary<string, string> BuildLegacyConfigValues()
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(legacyChatHistoryPath))
            {
                values["server.legacy.chatHistoryPath"] = legacyChatHistoryPath;
            }

            if (legacyChatHistoryLength > 0)
            {
                values["server.legacy.chatHistoryLength"] = legacyChatHistoryLength.ToString();
            }

            if (!string.IsNullOrWhiteSpace(legacyTradeDatabasePath))
            {
                values["server.legacy.tradeDatabasePath"] = legacyTradeDatabasePath;
            }

            return values;
        }

        private void clearLegacyConfigFields()
        {
            legacyChatHistoryPath = null;
            legacyTradeDatabasePath = null;
            legacyChatHistoryLength = 0;
        }
    }
}
