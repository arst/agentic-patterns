namespace ContextAssembly.AgentFramework;

public sealed record Candidate(string Source, string Text, double Relevance, bool Pinned = false);

public sealed record AssembledContext(
    IReadOnlyList<Candidate> Included,
    IReadOnlyList<(Candidate Candidate, string Why)> Dropped,
    int Tokens,
    int Budget);

/// Builds the context window on purpose, instead of appending until something breaks.
///
/// The default in most agents is accretion: history grows, retrieval results are concatenated,
/// tool output is pasted in, and the context is whatever that adds up to. That fails twice - it
/// blows the window on long runs, and long before that it buries the three lines that mattered
/// among forty that did not.
///
/// Assembly makes the window a budgeted allocation with an explicit order of business:
///   1. Pinned items go in first and are never evicted. The system prompt and the actual user
///      request are not candidates competing on relevance - a context that dropped the question
///      to fit more retrieval is worse than useless.
///   2. Near-duplicates collapse. Three sources saying the same thing spend three times the
///      tokens for one fact.
///   3. The rest compete on relevance, and what does not fit is DROPPED WITH A REASON, so a
///      thin answer can be traced to the eviction that caused it.
public static class ContextAssembler
{
    public static AssembledContext Assemble(IEnumerable<Candidate> candidates, int tokenBudget)
    {
        var included = new List<Candidate>();
        var dropped = new List<(Candidate, string)>();
        var seen = new List<string>();
        var used = 0;

        // Pinned first, then by relevance. Ties break on source name so two runs of the same
        // inputs assemble the same context - a context that varies run to run is a bug you
        // cannot reproduce.
        var ordered = candidates
            .OrderByDescending(c => c.Pinned)
            .ThenByDescending(c => c.Relevance)
            .ThenBy(c => c.Source, StringComparer.Ordinal);

        foreach (var candidate in ordered)
        {
            var cost = EstimateTokens(candidate.Text);

            if (!candidate.Pinned && seen.Any(t => NearDuplicate(t, candidate.Text)))
            {
                dropped.Add((candidate, "near-duplicate of an item already included"));
                continue;
            }

            if (!candidate.Pinned && used + cost > tokenBudget)
            {
                dropped.Add((candidate, $"would exceed the {tokenBudget}-token budget ({used} used)"));
                continue;
            }

            included.Add(candidate);
            seen.Add(candidate.Text);
            used += cost;
        }

        return new AssembledContext(included, dropped, used, tokenBudget);
    }

    /// ponytail: chars/4, the standard conservative estimate. Swap for the provider's tokenizer
    /// if you are running close enough to the limit that a 10% error matters.
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// Word-overlap, not embeddings: this is a de-duplicator, not a retriever, and the case it
    /// has to catch is the same fact arriving from two systems in slightly different words.
    static bool NearDuplicate(string a, string b)
    {
        var wordsA = Words(a);
        var wordsB = Words(b);
        if (wordsA.Count == 0 || wordsB.Count == 0) return false;

        var shared = wordsA.Intersect(wordsB).Count();
        return shared / (double)Math.Min(wordsA.Count, wordsB.Count) >= 0.75;
    }

    static HashSet<string> Words(string text) =>
        [.. text.Split([' ', '\n', '\t', ',', '.', ':', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 3)];
}
