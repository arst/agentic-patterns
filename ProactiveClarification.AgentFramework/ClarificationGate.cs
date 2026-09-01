namespace ProactiveClarification.AgentFramework;

/// A required piece of information, plus the words that mean a question is asking about it.
/// The vocabulary lives in the host, not in the prompt: the model proposes questions, the host
/// decides which ones are worth a human's attention.
public sealed record Slot(string Name, string[] Keywords);

public sealed record ScreenedQuestion(string Question, string? RejectedBecause)
{
    public bool Allowed => RejectedBecause is null;
}

public sealed record MergedAnswer(string Slot, string Value, string? IgnoredBecause)
{
    public bool Merged => IgnoredBecause is null;
}

public static class ClarificationGate
{
    /// Merges the parsed clarification answers back into slot state.
    ///
    /// Asking the question is only half a round trip. The half that is easy to leave out - and
    /// that makes the whole pattern hollow if you do - is writing the answer back, because until
    /// the host records it the slot is still missing and the run will "assume" something the user
    /// just told it.
    ///
    /// The answers are model-parsed out of free text, so they are untrusted the way any structured
    /// extraction is. But the guard here is narrower than it first looks. A user who answers MORE
    /// than was asked - volunteering a budget when the budget question was cut by the cap - is
    /// giving you information, and discarding it to then invent a default is the same failure the
    /// pattern exists to avoid, one step later. What actually needs guarding is a reply silently
    /// REWRITING a slot the request had already settled, which no clarification round asked about
    /// and no user should be surprised by.
    public static IReadOnlyList<MergedAnswer> Merge(
        IDictionary<string, string> filled,
        IReadOnlySet<string> knownSlots,
        IReadOnlySet<string> askedSlots,
        IEnumerable<(string Slot, string Value)> answers)
    {
        // Slots that were already settled before the round: only a question about one of them
        // licenses a change.
        var settled = filled.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<MergedAnswer>();

        foreach (var (slot, value) in answers)
        {
            var reason = !knownSlots.Contains(slot)
                ? "not a slot this host knows about"
                : string.IsNullOrWhiteSpace(value)
                    ? "the answer was empty"
                    : settled.Contains(slot) && !askedSlots.Contains(slot)
                        ? "would overwrite a slot the request already settled, and nothing asked about it"
                        : null;

            if (reason is null) filled[slot] = value.Trim();
            results.Add(new MergedAnswer(slot, value, reason));
        }

        return results;
    }

    public static IReadOnlyList<ScreenedQuestion> Screen(
        IReadOnlyCollection<Slot> slots,
        IReadOnlySet<string> filledSlots,
        IEnumerable<string> questions,
        int maxQuestions)
    {
        var screened = new List<ScreenedQuestion>();
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // one question per slot

        foreach (var question in questions)
        {
            var target = slots.FirstOrDefault(s =>
                s.Keywords.Any(k => question.Contains(k, StringComparison.OrdinalIgnoreCase)));

            var reason = Reject(target);
            if (reason is null) asked.Add(target!.Name);
            screened.Add(new ScreenedQuestion(question, reason));
        }

        return screened;

        string? Reject(Slot? target) => target switch
        {
            null => "asks about no required slot",
            _ when filledSlots.Contains(target.Name) => $"'{target.Name}' was already given",
            _ when asked.Contains(target.Name) => $"'{target.Name}' is already covered by an earlier question",
            _ when asked.Count >= maxQuestions => $"over the {maxQuestions}-question budget",
            _ => null
        };
    }
}
