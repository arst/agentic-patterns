using Microsoft.Extensions.AI;

/// <summary>
/// Redacts PII and truncates overlong output while keeping the rest of the response intact:
/// function calls, function results, and every other non-text <see cref="AIContent"/> kind pass
/// through untouched, and response-level metadata (finish reason, usage, model id, response id,
/// conversation id, continuation token, created-at, additional properties) is copied onto the
/// rewritten response. Lives in the global namespace, matching <see cref="SafetyChecks"/> in this project — a type named
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
    /// Caps the response's total text at <paramref name="maxCharacters"/>, including the
    /// truncation marker. Non-text content items are left untouched.
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
    /// every message. When that sum exceeds the limit, the response prefix is kept, a truncation
    /// marker is appended within the budget, and later text is cleared. Non-text content is
    /// untouched. Returns the original list unchanged (same reference) when already under budget,
    /// so callers can detect a no-op with
    /// <see cref="object.ReferenceEquals(object?, object?)"/>.
    /// </summary>
    internal static IList<ChatMessage> TruncateMessages(IList<ChatMessage> messages, int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCharacters);

        var totalLength = messages.Sum(m => m.Contents.OfType<TextContent>().Sum(t => t.Text.Length));
        if (totalLength <= maxCharacters)
            return messages;

        var marker = TruncationSuffix[..Math.Min(TruncationSuffix.Length, maxCharacters)];
        var remaining = maxCharacters - marker.Length;
        var truncated = false;
        return messages.Select(message =>
        {
            var contents = message.Contents.Select(content =>
            {
                if (content is not TextContent text) return content;
                if (truncated) return new TextContent("");
                if (text.Text.Length <= remaining)
                {
                    remaining -= text.Text.Length;
                    return content;
                }

                truncated = true;
                return new TextContent(text.Text[..remaining] + marker);
            }).ToList();
            return CloneWithContents(message, contents);
        }).ToList();
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

    // RawRepresentation is deliberately NOT copied: it would hand the caller a route back to
    // the un-redacted provider payload, defeating the redaction this class exists to do.
    private static ChatResponse WithMessages(ChatResponse response, IList<ChatMessage> messages) =>
        new(messages)
        {
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ModelId = response.ModelId,
            ResponseId = response.ResponseId,
            ConversationId = response.ConversationId,
            // The handle a background response is polled with. Dropping it on a redacted
            // turn would strand the caller exactly as dropping ConversationId would.
            ContinuationToken = response.ContinuationToken,
            CreatedAt = response.CreatedAt,
            AdditionalProperties = response.AdditionalProperties
        };
}
