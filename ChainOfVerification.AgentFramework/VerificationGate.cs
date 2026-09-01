namespace ChainOfVerification.AgentFramework;

/// One specific, checkable fact lifted out of the draft. `Value` is the part that can be wrong -
/// a year, a name, a number - and is what the verification question must NOT contain.
public sealed record Claim(int Id, string Text, string Value);

public sealed record VerificationQuestion(int ClaimId, string Question);

/// Host-side guard on the verification questions the planner produces.
///
/// Chain of Verification only pays for itself if the verification pass is *independent* of the
/// draft. A question that already carries the drafted value ("Was Cologne founded in 38 BC?")
/// is a leading question: the model that answers it is anchored on exactly the number under
/// suspicion, and agreement tells you nothing. The host rewrites or rejects those before they
/// are ever asked.
public static class VerificationGate
{
    /// Reasons this question cannot serve as an independent check. Empty means it may be asked.
    public static IReadOnlyList<string> Validate(Claim claim, string question)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(question))
            errors.Add("Question is empty.");
        else if (Leaks(question, claim.Value))
            errors.Add($"Question leaks the drafted value '{claim.Value}'; it would only ask the " +
                       "verifier to agree with the draft.");

        if (question.Length > 300)
            errors.Add("Question is long enough to be smuggling the draft back in as context.");

        return errors;
    }

    /// Token-level containment rather than substring: "38 BC" must not slip through inside
    /// "AD 38 BC-era", and a value that is a common word ("the") should not fail everything.
    static bool Leaks(string question, string value)
    {
        var valueTokens = Tokenize(value);
        if (valueTokens.Count == 0) return false;

        var questionTokens = Tokenize(question).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return valueTokens.All(questionTokens.Contains);
    }

    static List<string> Tokenize(string text) =>
        [.. text.Split(NonWord, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1 || char.IsDigit(t[0]))];

    static readonly char[] NonWord = [' ', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '-', '/'];
}
