namespace MemoryConsolidation.AgentFramework;

public enum EpisodeStatus { Active, Archived }

public sealed record Episode(string Id, string Text, DateTimeOffset At, double Importance, string Topic,
    EpisodeStatus Status = EpisodeStatus.Active);

/// A consolidated fact, with the episodes it was derived from still named.
///
/// `SourceEpisodeIds` is what separates a memory architecture from a lossy compressor. The
/// semantic memory is model-written prose about a dozen episodes; if it is slightly wrong and the
/// episodes are gone, the error is now canonical, unfalsifiable, and retrieved into every future
/// prompt. Keeping the derivation means a suspect fact can be re-derived, audited, or corrected
/// against what actually happened.
public sealed record SemanticMemory(string Text, string Topic, string[] SourceEpisodeIds, DateTimeOffset At)
{
    public int ConsolidatedFrom => SourceEpisodeIds.Length;
}

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

    /// Scores the ACTIVE episodes only. Archived ones are still on disk and still auditable; they
    /// are simply out of the hot retrieval set, which is what consolidation is for.
    public static IReadOnlyList<Scored> Score(IEnumerable<Episode> episodes, string query, DateTimeOffset now)
    {
        var queryWords = Words(query);

        return [.. episodes
            .Where(e => e.Status == EpisodeStatus.Active)
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
    /// Consolidation is lossy for the ACTIVE set on purpose - which is exactly why it needs a
    /// threshold rather than a schedule, and why it archives rather than deletes. Two episodes summarised into "the customer sometimes reports slow exports" have
    /// lost both dates and gained nothing; twelve of them have become a fact about the customer.
    public static IReadOnlyList<IGrouping<string, Episode>> Ripe(IEnumerable<Episode> episodes, int minimum) =>
        [.. episodes
            .Where(e => e.Status == EpisodeStatus.Active)
            .GroupBy(e => e.Topic, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= minimum)
            .OrderBy(g => g.Key, StringComparer.Ordinal)];
}
