using System;

namespace Utils
{
    public static class ConsoleHighlighting
    {
        private const string ResetAnsiSequence = "\u001b[0m";

        private static string ApplyAnsiHighlight(string input, string colourCode)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return $"\u001b[{colourCode}m{input}{ResetAnsiSequence}";
        }

        /// <summary>
        /// Highlights a string with ANSI colour codes according to the given <see cref="HighlightType"/>.
        /// Intended for making the server log more readable.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="highlightType"></param>
        /// <returns></returns>
        public static string Highlight(this string str, HighlightType highlightType)
        {
            switch (highlightType)
            {
                case HighlightType.ConnectionID:
                    return ApplyAnsiHighlight(str, "38;2;191;97;106");
                case HighlightType.SessionID:
                    return ApplyAnsiHighlight(str, "38;2;208;135;112");
                case HighlightType.UUID:
                    return ApplyAnsiHighlight(str, "38;2;235;203;139");
                case HighlightType.ChatMessageID:
                    return ApplyAnsiHighlight(str, "38;2;163;190;140");
                case HighlightType.TradeID:
                    return ApplyAnsiHighlight(str, "38;2;180;142;173");
                case HighlightType.Username:
                    return ApplyAnsiHighlight(str, "38;2;136;192;208");
                default:
                    return str;
            }
        }
    }

    public enum HighlightType
    {
        ConnectionID,
        SessionID,
        UUID,
        ChatMessageID,
        TradeID,
        Username
    }
}
