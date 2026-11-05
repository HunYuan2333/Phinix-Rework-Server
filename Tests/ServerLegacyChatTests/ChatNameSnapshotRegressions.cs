using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Phinix.ChatExtension.Server;
using Phinix.LegacyAdapter.Server;

internal static partial class Program
{
    private static string[] HistoryTexts(byte[] bytes)
    {
        var any = Any.Parser.ParseFrom(bytes); Check(any.TypeUrl == "Phinix/Chat.ChatHistoryPacket", "Expected actual history wire packet");
        using var input = new CodedInputStream(any.Value.ToByteArray()); var result = new List<string>(); uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag != 10) { input.SkipLastField(); continue; }
            using var body = new CodedInputStream(input.ReadBytes().ToByteArray()); uint inner;
            while ((inner = body.ReadTag()) != 0) if (inner == 26) result.Add(body.ReadString()); else body.SkipLastField();
        }
        return result.ToArray();
    }
    private static void ChatNameSnapshotRegressions()
    {
        string root = Path.Combine(Path.GetTempPath(), "phinix-chat-names-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(root);
        try
        {
            var users = new Directory(); users.Names["a"] = "Alice then";
            var chat = new PhinixFrameworkChatService(40, 2, 24, users);
            var parent = chat.AddMessage("a", "parent text"); users.Names["a"] = "Alice now";
            var reply = chat.AddMessage("b", "reply body", null, parent.MessageId, "parent text");
            Check(((IChatMessageNameLookup)chat).TryGetDisplayNameAtSend(parent.MessageId, out var then) && then == "Alice then", "Rename rewrote accepted-message name");
            string path = Path.Combine(root, "history.bin"); chat.Save(path);
            var restored = new PhinixFrameworkChatService(40, 2, 24, users); restored.Load(path);
            Check(restored.GetHistory().Length == 2 && ((IChatMessageNameLookup)restored).TryGetDisplayNameAtSend(parent.MessageId, out then) && then == "Alice then", "Atomic history save/reload lost historical name");
            var wire = new Wire();
            using (var adapter = new OriginalChatAdapter(wire, users, users, new Readiness(), restored, _ => { }))
            {
                adapter.Start(); users.Login("b");
                Check(HistoryTexts(wire.Sent.Single().Bytes).Contains("[Reply Alice then] parent text\nreply body"), "Old client reply used message UUID or current name despite historical snapshot");
            }
            // Actual older protobuf store has no server-local name extension.
            string old = Path.Combine(root, "old.bin");
            var store = new Phinix.Framework.BuiltInChatHistoryStore(); store.ChatMessages.Add(parent); store.ChatMessages.Add(reply); File.WriteAllBytes(old, store.ToByteArray());
            restored = new PhinixFrameworkChatService(40, 2, 24, users); restored.Load(old);
            Check(!((IChatMessageNameLookup)restored).TryGetDisplayNameAtSend(parent.MessageId, out _), "Legacy history falsely backfilled historical name from today's directory");
            wire = new Wire();
            using (var adapter = new OriginalChatAdapter(wire, users, users, new Readiness(), restored, _ => { }))
            {
                adapter.Start(); users.Login("b");
                Check(HistoryTexts(wire.Sent.Single().Bytes).Contains("[Reply Alice now (current name)] parent text\nreply body"), "Legacy name fallback not visibly distinguished from historical fact");
            }
            // Retention removes the associated name; the local extension never grows independently.
            var bounded = new PhinixFrameworkChatService(1, 2, 24, users); var removed = bounded.AddMessage("a", "first"); bounded.AddMessage("a", "second");
            Check(!((IChatMessageNameLookup)bounded).TryGetDisplayNameAtSend(removed.MessageId, out _), "Evicted message retained unbounded name evidence");
        }
        finally { System.IO.Directory.Delete(root, true); }
    }
}
