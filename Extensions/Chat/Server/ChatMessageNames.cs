using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Google.Protobuf;
using Message = Phinix.Framework.BuiltInChatMessagePayload;

namespace Phinix.ChatExtension.Server;

public interface IChatMessageNameLookup
{
    bool TryGetDisplayNameAtSend(string messageId, out string displayName);
}

public partial class PhinixFrameworkChatService : IChatMessageNameLookup
{
    // Server-local storage extension, NEVER added to network messages or Common.
    // Older stores without field 1000 have unknown historical names; never backfill
    // them from today's player directory. Names and messages share the atomic file.
    private const uint NamesTag = (1000 << 3) | 2;
    private sealed record NameEntry(string MessageId, string SenderUuid, string DisplayName);
    private sealed record NamesState(int Version, NameEntry[] Entries);
    private readonly Dictionary<string, NameEntry> namesAtSend = new(StringComparer.Ordinal);

    private string CaptureDisplayName(string principal)
    {
        try
        {
            if (userManager != null && userManager.TryGetDisplayName(principal, out var name) && !string.IsNullOrWhiteSpace(name))
                return Utils.TextHelper.StripRichText(name);
        }
        catch { /* A directory outage must not invent a historical name. */ }
        return null;
    }
    private void RecordDisplayName(Message message, string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) namesAtSend[message.MessageId] = new(message.MessageId, message.SenderUuid, name);
    }
    public bool TryGetDisplayNameAtSend(string messageId, out string displayName)
    {
        lock (messageHistoryLock)
        {
            if (messageId != null && namesAtSend.TryGetValue(messageId, out var entry)) { displayName = entry.DisplayName; return true; }
            displayName = null; return false;
        }
    }
    private byte[] NameSnapshotBytes()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new NamesState(1, namesAtSend.Values.OrderBy(n => n.MessageId, StringComparer.Ordinal).ToArray()));
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Chat name snapshots exceed budget.");
        return bytes;
    }
    private void ReadNameSnapshots(Stream stream, IEnumerable<Message> messages)
    {
        stream.Position = 0;
        using var input = new CodedInputStream(stream, leaveOpen: true);
        byte[] bytes = null; uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag != NamesTag) { input.SkipLastField(); continue; }
            if (bytes != null) throw new InvalidDataException("Duplicate chat name snapshots.");
            bytes = input.ReadBytes().ToByteArray();
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Chat name snapshots exceed budget.");
        }
        var loaded = new Dictionary<string, NameEntry>(StringComparer.Ordinal);
        if (bytes != null)
        {
            NamesState state;
            try { state = JsonSerializer.Deserialize<NamesState>(bytes); }
            catch (JsonException e) { throw new InvalidDataException("Invalid chat name snapshots.", e); }
            if (state?.Version != 1 || state.Entries == null) throw new InvalidDataException("Unsupported chat name snapshots.");
            var byId = messages.ToLookup(m => m.MessageId, StringComparer.Ordinal);
            foreach (var entry in state.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.MessageId) || string.IsNullOrWhiteSpace(entry.DisplayName) || entry.DisplayName.Length > 4096 ||
                    byId[entry.MessageId].Count() != 1 || byId[entry.MessageId].Single().SenderUuid != entry.SenderUuid || !loaded.TryAdd(entry.MessageId, entry))
                    throw new InvalidDataException("Chat name snapshot does not match retained message.");
            }
        }
        namesAtSend.Clear(); foreach (var entry in loaded) namesAtSend.Add(entry.Key, entry.Value);
    }
}
