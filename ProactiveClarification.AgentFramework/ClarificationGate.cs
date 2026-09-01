namespace ProactiveClarification.AgentFramework;

/// A required piece of information, plus the words that mean a question is asking about it.
/// The vocabulary lives in the host, not in the prompt: the model proposes questions, the host
/// decides which ones are worth a human's attention.
public sealed record Slot(string Name, string[] Keywords);

public sealed record ScreenedQuestion(string Question, string? RejectedBecause)
{
    public bool Allowed => RejectedBecause is null;
}

public static class ClarificationGate
{
    /// Screens the model's proposed clarifying questions against what the request already said.
    ///
    /// Two failure modes this exists to stop:
    ///   - asking about something the user already told you (the fastest way to look like a form);
    ///   - asking about nothing in particular ("could you tell me more?"), which spends a
    ///     round-trip and returns no slot.
    /// Anything that survives is capped, because a wall of questions is itself a failure.
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
