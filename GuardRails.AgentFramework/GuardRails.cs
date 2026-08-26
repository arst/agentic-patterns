using Microsoft.Extensions.AI;

/// <summary>
/// Redacts PII and truncates overlong output while keeping the rest of the response intact:
/// function calls, function results, and every other non-text <see cref="AIContent"/> kind pass
/// through untouched, and response-level metadata (finish reason, usage, model id, response id,
/// created-at, additional properties) is copied onto the rewritten response. Lives in the global
/// namespace, matching <see cref="SafetyChecks"/> in this project — a type named
/// <c>GuardRails</c> inside a <c>GuardRails.*</c> namespace would make <c>GuardRails.Redact(...)</c>
/// ambiguous at the call site.
/// </summary>
public static class GuardRails
{
    private const string TruncationSuffix = "\n\n[Response truncated for safety.]";

    /// <summary>Redacts PII from every <see cref="TextContent"/> in the response.</summary>
    public static ChatResponse Redact(ChatResponse response) =>
        WithMessages(response, RedactMessages(response.Messages));

    /// <summary>
    /// Caps the response's total text at <paramref name="maxCharacters"/>. Only the last
    /// <see cref="TextContent"/> in the response absorbs the cut — earlier text and every
    /// non-text content item are left untouched. See <see cref="TruncateMessages"/> for the
    /// exact budget rule.
    /// </summary>
    public static ChatResponse Truncate(ChatResponse response, int maxCharacters) =>
        WithMessages(response, TruncateMessages(response.Messages, maxCharacters));

    /// <summary>
    /// Redacts PII from a list of messages. Used both by <see cref="Redact"/> and by
    /// callers that hold an <see cref="Microsoft.Agents.AI.AgentResponse"/> rather than a
    /// <see cref="ChatResponse"/> (they don't share a base type, so there's nothing to overload on).
    /// </summary>
    internal static IList<ChatMessage> RedactMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(RedactMessage).ToList();

    /// <summary>Redacts PII from a single message's <see cref="TextContent"/> items, in place of a copy.</summary>
    internal static ChatMessage RedactMessage(ChatMessage message) =>
        MapTextContent(message, SafetyChecks.RedactPii);

    /// <summary>
    /// Truncates a list of messages to at most <paramref name="maxCharacters"/> characters of
    /// total text. "Total text" is the sum of every <see cref="TextContent.Text"/> length across
    /// every message. When that sum exceeds the limit, only the last message's last
    /// <see cref="TextContent"/> is shortened — down to whatever budget remains once every other
    /// text item is counted — and a truncation marker is appended; everything before it, and
    /// every non-text content item, is untouched. Returns the original list unchanged (same
    /// reference) when already under budget, so callers can detect a no-op with
    /// <see cref="object.ReferenceEquals(object?, object?)"/>.
    /// </summary>
    internal static IList<ChatMessage> TruncateMessages(IList<ChatMessage> messages, int maxCharacters)
    {
        var totalLength = messages.Sum(m => m.Contents.OfType<TextContent>().Sum(t => t.Text.Length));
        if (totalLength <= maxCharacters)
            return messages;

        var lastMessageIndex = -1;
        var lastTextIndex = -1;
        for (var i = messages.Count - 1; i >= 0 && lastMessageIndex < 0; i--)
        {
            var contents = messages[i].Contents;
            for (var j = contents.Count - 1; j >= 0; j--)
            {
                if (contents[j] is not TextContent) continue;
                lastMessageIndex = i;
                lastTextIndex = j;
                break;
            }
        }

        // No TextContent anywhere in the response — nothing this function is responsible for can trim.
        if (lastMessageIndex < 0)
            return messages;

        var lastText = (TextContent)messages[lastMessageIndex].Contents[lastTextIndex];
        var budget = Math.Max(0, maxCharacters - (totalLength - lastText.Text.Length));
        var truncatedText = (budget < lastText.Text.Length ? lastText.Text[..budget] : lastText.Text) + TruncationSuffix;

        var newContents = messages[lastMessageIndex].Contents.ToList();
        newContents[lastTextIndex] = new TextContent(truncatedText);
        var newMessages = messages.ToList();
        newMessages[lastMessageIndex] = CloneWithContents(messages[lastMessageIndex], newContents);
        return newMessages;
    }

    // Core mapper: rewrites only TextContent items (via `transform`) in a message's Contents,
    // leaving function calls, function results, and every other content kind untouched.
    private static ChatMessage MapTextContent(ChatMessage message, Func<string, string> transform)
    {
        var mapped = message.Contents
            .Select(c => c is TextContent text ? new TextContent(transform(text.Text)) : c)
            .ToList();
        return CloneWithContents(message, mapped);
    }

    private static ChatMessage CloneWithContents(ChatMessage message, IList<AIContent> contents)
    {
        var clone = message.Clone();
        clone.Contents = contents;
        return clone;
    }

    private static ChatResponse WithMessages(ChatResponse response, IList<ChatMessage> messages) =>
        new(messages)
        {
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ModelId = response.ModelId,
            ResponseId = response.ResponseId,
            CreatedAt = response.CreatedAt,
            AdditionalProperties = response.AdditionalProperties
        };
}
