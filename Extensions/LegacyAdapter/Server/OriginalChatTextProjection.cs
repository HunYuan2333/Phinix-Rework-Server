using System;
using System.Collections.Generic;
using Message = Phinix.Framework.BuiltInChatMessagePayload;

namespace Phinix.LegacyAdapter.Server;

// Original has one text field. Preserve structured context as visible text, without
// pretending that the original client implements reply navigation or mention alerts.
public static class OriginalChatTextProjection
{
    public static Message Project(Message message) => Project(message, null, null);
    public static Message Project(Message message, Func<string, string> replyAuthor, Func<string, string> mentionName)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        var result = message.Clone();
        var context = new List<string>();
        if (result.IsNotice) context.Add("[Notice]");
        if (!string.IsNullOrEmpty(result.ReplyToMessageId) || !string.IsNullOrEmpty(result.ReplyToSnippet))
            context.Add("[Reply " + (replyAuthor?.Invoke(result.ReplyToMessageId) ?? result.ReplyToMessageId) + "] " + result.ReplyToSnippet);
        if (result.MentionedUuids.Count != 0)
            context.Add("[Mentions: " + string.Join(", ", System.Linq.Enumerable.Select(result.MentionedUuids, id => mentionName?.Invoke(id) ?? id)) + "]");
        if (context.Count != 0) result.Message = string.Join("\n", context) + "\n" + result.Message;
        result.IsNotice = false;
        result.ReplyToMessageId = ""; result.ReplyToSnippet = ""; result.MentionedUuids.Clear();
        return result;
    }
}
