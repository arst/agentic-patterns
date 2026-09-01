using System.Text.RegularExpressions;

namespace StepBack.AgentFramework;

/// Checks that the "step back" actually stepped back.
///
/// The failure mode of step-back prompting is that the model answers the concrete question while
/// pretending to state a principle: "the block reaches 7 m/s because..." is not a principle, it
/// is the answer wearing a hat, and it buys nothing - you have paid for two calls and got one.
/// The tell is cheap to detect: a genuine principle does not carry the question's specific
/// quantities.
public static partial class PrincipleGate
{
    [GeneratedRegex(@"\d+(?:[.,]\d+)?")] private static partial Regex Number();

    /// Numbers from the question that reappear in the principle. Empty means it stayed abstract.
    public static IReadOnlyList<string> LeakedSpecifics(string question, string principle)
    {
        var fromQuestion = Number().Matches(question).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        if (fromQuestion.Count == 0) return [];

        return [.. Number().Matches(principle).Select(m => m.Value)
            .Where(fromQuestion.Contains)
            .Distinct(StringComparer.Ordinal)];
    }
}
