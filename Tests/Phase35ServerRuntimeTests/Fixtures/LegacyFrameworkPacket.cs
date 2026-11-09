// Frozen pre-Relay packet definitions from Common 67f243d9, namespace isolated for wire compatibility tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace Utils.Framework.LegacyWire
{
    [DataContract]
    public sealed class FrameworkMetadataEntry
    {
        [DataMember(Order = 0)]
        public string Key { get; set; }

        [DataMember(Order = 1)]
        public string Value { get; set; }
    }

    [DataContract]
    public sealed class FrameworkPacket
    {
        [DataMember(Order = 0)]
        public int Version { get; set; } = FrameworkProtocol.Version;

        [DataMember(Order = 1)]
        public string Kind { get; set; }

        [DataMember(Order = 2)]
        public string MessageType { get; set; }

        [DataMember(Order = 3)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        [DataMember(Order = 4)]
        public string SessionId { get; set; }

        [DataMember(Order = 5)]
        public string SenderUuid { get; set; }

        [DataMember(Order = 6)]
        public long TimestampUtcTicks { get; set; } = DateTime.UtcNow.Ticks;

        [DataMember(Order = 7)]
        public string PayloadJson { get; set; }

        [DataMember(Order = 8)]
        public List<FrameworkMetadataEntry> Metadata { get; set; } = new List<FrameworkMetadataEntry>();

        [DataMember(Order = 9)]
        public global::Phinix.Framework.FrameworkFlow Flow { get; set; } = global::Phinix.Framework.FrameworkFlow.Unspecified;

        [DataMember(Order = 10)]
        public global::Phinix.Framework.FrameworkCommandKind CommandKind { get; set; } = global::Phinix.Framework.FrameworkCommandKind.Unspecified;

        [DataMember(Order = 11)]
        public byte[] PayloadBytes { get; set; } = Array.Empty<byte>();
    }

    [DataContract]
    public sealed class FrameworkCapabilitiesPayload
    {
        [DataMember(Order = 0)]
        public List<string> Capabilities { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class FrameworkDisplayMessage
    {
        [DataMember(Order = 0)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        [DataMember(Order = 1)]
        public string SenderUuid { get; set; }

        [DataMember(Order = 2)]
        public string Text { get; set; }

        [DataMember(Order = 3)]
        public long TimestampUtcTicks { get; set; } = DateTime.UtcNow.Ticks;

        [DataMember(Order = 4)]
        public string Source { get; set; } = "framework";

        [DataMember(Order = 5)]
        public bool SuppressDefaultDisplay { get; set; }

        [DataMember(Order = 6)]
        public string TranslationKey { get; set; }

        [DataMember(Order = 7)]
        public List<string> TranslationArgs { get; set; } = new List<string>();

        [DataMember(Order = 8)]
        public List<string> MentionedUuids { get; set; } = new List<string>();

        [DataMember(Order = 9)]
        public string ReplyToMessageId { get; set; }

        [DataMember(Order = 10)]
        public string ReplyToSnippet { get; set; }

        [DataMember(Order = 11)]
        public bool IsNotice { get; set; }

        [DataMember(Order = 12)]
        public int NoticeDurationSeconds { get; set; }
    }

    [DataContract]
    public sealed class FrameworkItemPayload
    {
        [DataMember(Order = 0)]
        public string CodecId { get; set; }

        [DataMember(Order = 1)]
        public string PayloadJson { get; set; }

        [DataMember(Order = 2)]
        public List<FrameworkMetadataEntry> Metadata { get; set; } = new List<FrameworkMetadataEntry>();

        [DataMember(Order = 3)]
        public byte[] PayloadBytes { get; set; } = Array.Empty<byte>();
    }

    public static class FrameworkMetadataKeys
    {
        public const string CorrelationId = "correlation_id";
        public const string SnapshotVersion = "snapshot_version";
        public const string StateKind = "state_kind";
    }

    public static class FrameworkMetadataStateKinds
    {
        public const string Snapshot = "snapshot";
        public const string Delta = "delta";
        public const string Projection = "projection";
        public const string Event = "event";
    }

    public static class FrameworkMetadataHelpers
    {
        public static string GetCorrelationId(this FrameworkPacket packet)
        {
            return packet.TryGetMetadataValue(FrameworkMetadataKeys.CorrelationId, out string correlationId) && !string.IsNullOrWhiteSpace(correlationId)
                ? correlationId
                : packet?.MessageId;
        }

        public static void SetCorrelationId(this FrameworkPacket packet, string correlationId)
        {
            packet.SetMetadataValue(FrameworkMetadataKeys.CorrelationId, correlationId);
        }

        public static bool TryGetSnapshotVersion(this FrameworkPacket packet, out long snapshotVersion)
        {
            snapshotVersion = 0L;
            return packet.TryGetMetadataValue(FrameworkMetadataKeys.SnapshotVersion, out string rawValue) &&
                   long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshotVersion);
        }

        public static void SetSnapshotVersion(this FrameworkPacket packet, long snapshotVersion)
        {
            packet.SetMetadataValue(FrameworkMetadataKeys.SnapshotVersion, snapshotVersion.ToString(CultureInfo.InvariantCulture));
        }

        public static bool TryGetStateKind(this FrameworkPacket packet, out string stateKind)
        {
            return packet.TryGetMetadataValue(FrameworkMetadataKeys.StateKind, out stateKind) &&
                   !string.IsNullOrWhiteSpace(stateKind);
        }

        public static void SetStateKind(this FrameworkPacket packet, string stateKind)
        {
            packet.SetMetadataValue(FrameworkMetadataKeys.StateKind, stateKind);
        }

        public static bool TryGetMetadataValue(this FrameworkPacket packet, string key, out string value)
        {
            value = null;
            if (packet?.Metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            FrameworkMetadataEntry entry = packet.Metadata.Find(candidate => string.Equals(candidate?.Key, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return false;
            }

            value = entry.Value;
            return true;
        }

        public static void SetMetadataValue(this FrameworkPacket packet, string key, string value)
        {
            if (packet == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            packet.Metadata = packet.Metadata ?? new List<FrameworkMetadataEntry>();
            FrameworkMetadataEntry entry = packet.Metadata.Find(candidate => string.Equals(candidate?.Key, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                packet.Metadata.Add(new FrameworkMetadataEntry
                {
                    Key = key,
                    Value = value ?? string.Empty
                });
                return;
            }

            entry.Value = value ?? string.Empty;
        }

        public static bool TryGetMetadataValue(this FrameworkItemPayload payload, string key, out string value)
        {
            value = null;
            if (payload?.Metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            FrameworkMetadataEntry entry = payload.Metadata.Find(candidate => string.Equals(candidate?.Key, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return false;
            }

            value = entry.Value;
            return true;
        }

        public static void SetMetadataValue(this FrameworkItemPayload payload, string key, string value)
        {
            if (payload == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            payload.Metadata = payload.Metadata ?? new List<FrameworkMetadataEntry>();
            FrameworkMetadataEntry entry = payload.Metadata.Find(candidate => string.Equals(candidate?.Key, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                payload.Metadata.Add(new FrameworkMetadataEntry
                {
                    Key = key,
                    Value = value ?? string.Empty
                });
                return;
            }

            entry.Value = value ?? string.Empty;
        }
    }
}
