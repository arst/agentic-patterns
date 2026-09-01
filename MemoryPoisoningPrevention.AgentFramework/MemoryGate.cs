namespace MemoryPoisoningPrevention.AgentFramework;

/// How much a source is believed. A trust CLASS, decided by the host before anything is read -
/// never inferred from how authoritative the text sounds.
public enum Trust { Authoritative, Operator, UserSaid, ToolOutput, WebContent }

/// Where a claim actually came from, as an identity rather than a category: a specific page, a
/// specific contract record, a specific person.
///
/// Keeping this separate from `Trust` is the difference between corroboration and theatre. A
/// trust class cannot answer "are these two claims independent", and using it as if it could
/// fails in both directions: a scraper reading the same page it was seeded from counts as a
/// second opinion because its class differs, while two genuinely unrelated publishers cannot
/// corroborate each other at all because their class is the same. Independence is a property of
/// the evidence, so it has to be modelled on the evidence.
public sealed record Source(string Id, Trust Trust);

public enum Tier { Active, Quarantined, Rejected }

public sealed record MemoryItem(
    string Key,
    string Value,
    Source Source,
    Tier Tier = Tier.Quarantined,
    int Corroborations = 1);

public sealed record Admission(MemoryItem Item, string Reason);

public static class MemoryGate
{
    static readonly HashSet<Trust> Trusted = [Trust.Authoritative, Trust.Operator];

    public static Admission Admit(MemoryItem candidate, IReadOnlyCollection<MemoryItem> existing)
    {
        var incumbent = existing.FirstOrDefault(m =>
            m.Key.Equals(candidate.Key, StringComparison.OrdinalIgnoreCase) && m.Tier == Tier.Active);

        if (incumbent is { Source.Trust: Trust.Authoritative } &&
            !incumbent.Value.Equals(candidate.Value, StringComparison.OrdinalIgnoreCase) &&
            candidate.Source.Trust != Trust.Authoritative)
            return new Admission(candidate with { Tier = Tier.Rejected },
                $"contradicts the authoritative value '{incumbent.Value}'");

        if (Trusted.Contains(candidate.Source.Trust))
            return new Admission(candidate with { Tier = Tier.Active },
                $"trusted source ({candidate.Source.Id}, {candidate.Source.Trust})");

        // Independence is counted by evidence IDENTITY, not by trust class and not by occurrence.
        // The same page seen twice is one claim however it was fetched; two different publishers
        // are two claims even though both are WebContent.
        var corroborating = existing
            .Where(m => m.Key.Equals(candidate.Key, StringComparison.OrdinalIgnoreCase) &&
                        m.Value.Equals(candidate.Value, StringComparison.OrdinalIgnoreCase) &&
                        !m.Source.Id.Equals(candidate.Source.Id, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Source.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return corroborating >= 1
            ? new Admission(candidate with { Tier = Tier.Active, Corroborations = corroborating + 1 },
                $"corroborated by {corroborating} independent source(s)")
            : new Admission(candidate with { Tier = Tier.Quarantined },
                $"untrusted source ({candidate.Source.Id}), no independent corroboration");
    }

    /// What the agent is actually allowed to see. Quarantined items are not "included with a
    /// warning" - a caveat in the context window is still content the model will use.
    public static IReadOnlyList<MemoryItem> Retrievable(IEnumerable<MemoryItem> store) =>
        [.. store.Where(m => m.Tier == Tier.Active)];
}
