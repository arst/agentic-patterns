namespace MemoryConsolidation.AgentFramework;

public sealed record Episode(string Text, DateTimeOffset At, double Importance, string Topic);

public sealed record SemanticMemory(string Text, string Topic, int ConsolidatedFrom, DateTimeOffset At);

public sealed record Scored(Episode Episode, double Recency, double Relevance, double Total);

/// Generative-agents retrieval: recency + importance + relevance, added rather than filtered.
///
/// Vector search alone retrieves the most similar memory, which for a long-lived agent is
/// regularly the wrong one - a highly relevant thing from eight months ago beats a slightly less
/// relevant thing from this morning, and the agent answers with stale information it is very
/// confident about. Recency puts a thumb on the scale for what just happened; importance keeps
/// the rare significant event retrievable long after it stops being recent.
public static class EpisodicRetrieval
{
    /// Half-life in hours: a memory a day old counts about a fifth of a fresh one.
    const double DecayPerHour = 0.995;

    public static IReadOnlyList<Scored> Score(IEnumerable<Episode> episodes, string query, DateTimeOffset now)
    {
        var queryWords = Words(query);

        return [.. episodes
            .Select(e =>
            {
                var recency = Math.Pow(DecayPerHour, Math.Max(0, (now - e.At).TotalHours));
                var words = Words(e.Text);
                var relevance = queryWords.Count == 0 || words.Count == 0
                    ? 0
                    : words.Intersect(queryWords).Count() / (double)queryWords.Count;

                return new Scored(e, recency, relevance, recency + e.Importance + relevance);
            })
            .OrderByDescending(s => s.Total)
            .ThenBy(s => s.Episode.Text, StringComparer.Ordinal)];
    }

    /// ponytail: word overlap standing in for an embedding similarity, so the sample needs no
    /// vector store. Swap in the embedding generator from the RAG sample for real relevance;
    /// the scoring formula around it does not change.
    static HashSet<string> Words(string text) =>
        [.. text.Split([' ', ',', '.', ';', ':', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant().Trim())
            .Where(w => w.Length > 3)];
}

public static class Consolidation
{
    /// Which episodes are ripe for consolidation: a topic with enough accumulated episodes that
    /// the generalisation is worth making and the individual events are no longer worth keeping.
    ///
    /// Consolidation is lossy on purpose, which is exactly why it needs a threshold rather than a
    /// schedule. Two episodes summarised into "the customer sometimes reports slow exports" have
    /// lost both dates and gained nothing; twelve of them have become a fact about the customer.
    public static IReadOnlyList<IGrouping<string, Episode>> Ripe(IEnumerable<Episode> episodes, int minimum) =>
        [.. episodes
            .GroupBy(e => e.Topic, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= minimum)
            .OrderBy(g => g.Key, StringComparer.Ordinal)];
}
