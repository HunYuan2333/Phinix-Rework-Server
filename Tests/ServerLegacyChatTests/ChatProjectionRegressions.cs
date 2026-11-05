using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Phinix.ChatExtension.Server;
using Phinix.LegacyAdapter.Server;
using Message = Phinix.Framework.BuiltInChatMessagePayload;

internal static partial class Program
{
    private static string ChatText(byte[] bytes)
    {
        var any = Any.Parser.ParseFrom(bytes);
        Check(any.TypeUrl == "Phinix/Chat.ChatMessagePacket", "Unexpected live chat packet");
        using var input = new CodedInputStream(any.Value.ToByteArray()); uint tag; string text = "";
        while ((tag = input.ReadTag()) != 0)
            if (tag == 26) text = input.ReadString(); else input.SkipLastField();
        return text;
    }
    private static void ChatProjectionRegressions()
    {
        var source = Plain("actual body"); source.ReplyToMessageId = "parent-id"; source.ReplyToSnippet = "parent text";
        source.MentionedUuids.Add("a"); source.MentionedUuids.Add("b"); source.IsNotice = true;
        byte[] original = source.ToByteArray(); var projected = OriginalChatTextProjection.Project(source);
        Check(projected.Message == "[Notice]\n[Reply parent-id] parent text\n[Mentions: a, b]\nactual body", "Projection lost explicit context or body");
        Check(projected.MessageId == source.MessageId && projected.SenderUuid == source.SenderUuid && projected.Timestamp.Equals(source.Timestamp), "Projection changed authoritative message identity/time");
        Check(source.ToByteArray().SequenceEqual(original), "Projection mutated modern authoritative message");
        Check(OriginalChatCodec.CanExpress(projected) && OriginalChatTextProjection.Project(projected).ToByteArray().SequenceEqual(projected.ToByteArray()), "Projection not expressible/idempotent");
        var plain = Plain();
        Check(OriginalChatTextProjection.Project(plain).ToByteArray().SequenceEqual(plain.ToByteArray()), "Plain projection changed data");

        var net = new Wire(); var users = new Directory(); var warnings = new List<string>();
        var chat = new PhinixFrameworkChatService(40, 2, 24, users);
        // Deliberately unrepresentable history size; no filtering or fake empty bootstrap.
        chat.AddMessage("modern", new string('x', OriginalChatCodec.MaximumPacketBytes + 512));
        using var adapter = new OriginalChatAdapter(net, users, users, new Readiness(), chat, _ => { }, (m, _) => warnings.Add(m));
        adapter.Start(); users.Login("a");
        Check(net.Sent.Count == 0 && warnings.Contains("OriginalChatHistoryUnrepresentable"), "Oversize history did not preserve explicit unavailable state");
        net.Receive("a", Request("after-bad-history", "new plain message"));
        var result = Response(net.Sent.Single().Bytes);
        Check(result.TryGetValue(1, out var success) && success == "True" && result[2] == "after-bad-history" && chat.GetHistory().Length == 2, "History failure blocked actual new-message success");
        net.Sent.Clear(); net.Receive("a", Request("after-bad-history", "new plain message"));
        Check(Response(net.Sent.Single().Bytes)[3] == result[3] && chat.GetHistory().Length == 2, "Replay after unavailable history duplicated message");
        net.Sent.Clear(); chat.AddMessage("modern", "independent live message");
        Check(ChatText(net.Sent.Single().Bytes) == "independent live message" && net.Disconnected.Count == 0, "Unavailable history blocked independently expressible live broadcast");
        Check(warnings.Count(w => w == "OriginalChatHistoryUnrepresentable") == 1, "Every live message retried expensive failed history bootstrap");
    }
}
