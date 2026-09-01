namespace LeastToMost.AgentFramework;

public sealed record SubProblem(int Order, string Question);

public static class Decomposition
{
    /// Turns the model's proposed decomposition into one the host is willing to execute.
    ///
    /// Least-to-most only works if the chain actually ends at the question you asked. Models
    /// reliably produce good sub-steps and then stop one step short - they solve the pieces and
    /// never assemble them. Rather than prompt harder, the host guarantees the last subproblem
    /// IS the original question: appended if the model forgot, moved to the end if it put it first.
    public static IReadOnlyList<SubProblem> Normalize(IEnumerable<string> proposed, string question, int max)
    {
        var steps = proposed
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Where(s => !Equivalent(s, question))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max - 1)
            .ToList();

        steps.Add(question);

        return [.. steps.Select((s, i) => new SubProblem(i + 1, s))];
    }

    /// Cheap normalisation, not semantics: it catches the model echoing the question back with
    /// different punctuation, which is the only case that matters here.
    static bool Equivalent(string a, string b) =>
        string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    static string Squash(string s) =>
        new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
