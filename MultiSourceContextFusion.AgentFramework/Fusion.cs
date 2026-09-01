namespace MultiSourceContextFusion.AgentFramework;

/// How much a source is believed when it disagrees with another. Ordered deliberately: a system
/// of record outranks what a customer said about themselves, which outranks a scraped page.
public enum Trust { SystemOfRecord = 4, Operator = 3, UserStated = 2, Retrieved = 1, Inferred = 0 }

public sealed record Fact(string Field, string Value, string Source, Trust Trust, DateOnly AsOf);

public sealed record Resolution(string Field, Fact Winner, IReadOnlyList<Fact> Losers, string Rule)
{
    public bool WasContested => Losers.Count > 0;
}

/// Merging several sources into one context is easy right up to the moment two of them disagree,
/// and then it is the whole problem.
///
/// Concatenating both values and letting the model sort it out is the common non-answer: the
/// model picks whichever it read last, or averages two addresses into one that does not exist,
/// and either way the choice is invisible afterwards. Fusion makes the choice in the host, by a
/// rule you can state - trust first, recency second - and keeps the losers so the resolution can
/// be explained and audited.
///
/// The second half matters as much: a contested field is surfaced to the model as contested. A
/// silently resolved conflict tells the agent it knows something it does not.
public static class ContextFusion
{
    public static IReadOnlyList<Resolution> Fuse(IEnumerable<Fact> facts) =>
        [.. facts
            .GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var ranked = group
                    .OrderByDescending(f => f.Trust)
                    .ThenByDescending(f => f.AsOf)
                    .ThenBy(f => f.Source, StringComparer.Ordinal)
                    .ToList();

                var winner = ranked[0];

                // Only genuinely different VALUES are conflicts. Two sources agreeing is
                // corroboration, and reporting it as a conflict trains everyone to ignore the list.
                var losers = ranked.Skip(1)
                    .Where(f => !f.Value.Equals(winner.Value, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var rule = losers.Count == 0
                    ? "uncontested"
                    : losers[0].Trust < winner.Trust
                        ? $"higher trust ({winner.Trust} over {losers[0].Trust})"
                        : $"same trust, more recent ({winner.AsOf:yyyy-MM-dd} over {losers[0].AsOf:yyyy-MM-dd})";

                return new Resolution(group.Key, winner, losers, rule);
            })];

    /// The fused context as the model should see it: resolved values, with contested fields
    /// carrying their provenance and the value that lost.
    public static string Render(IEnumerable<Resolution> resolutions) =>
        string.Join("\n", resolutions.Select(r => r.WasContested
            ? $"{r.Field}: {r.Winner.Value}  [{r.Winner.Source}, {r.Winner.AsOf:yyyy-MM-dd}] " +
              $"— CONTESTED: {string.Join("; ", r.Losers.Select(l => $"{l.Source} says '{l.Value}'"))}"
            : $"{r.Field}: {r.Winner.Value}  [{r.Winner.Source}, {r.Winner.AsOf:yyyy-MM-dd}]"));
}
