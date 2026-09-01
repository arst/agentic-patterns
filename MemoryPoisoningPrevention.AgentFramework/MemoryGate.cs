namespace MemoryPoisoningPrevention.AgentFramework;

/// Where a candidate memory came from. Trust is a property of the SOURCE, decided by the host
/// before anything is read - never inferred from how authoritative the text sounds.
public enum Provenance { Authoritative, Operator, UserSaid, ToolOutput, WebContent }

public enum Tier { Active, Quarantined, Rejected }

public sealed record MemoryItem(
    string Key,
    string Value,
    Provenance Source,
    Tier Tier = Tier.Quarantined,
    int Corroborations = 1);

public sealed record Admission(MemoryItem Item, string Reason);

/// The gate between "the agent learned something" and "the agent will act on it forever".
///
/// Persistent memory turns a one-shot injection into a permanent one. An attacker who gets a
/// sentence into a web page the agent reads once has, without this gate, written to a store that
/// is retrieved into every future prompt - and unlike a prompt injection, nobody re-reads it,
/// because it now looks like something the agent knows.
///
/// Three rules, all enforced here rather than asked for in a prompt:
///   1. Untrusted sources may propose, never publish: they land in quarantine.
///   2. Quarantine leaves only by corroboration from an INDEPENDENT source, or by a human.
///   3. Nothing overwrites an authoritative fact. A contradiction is a security event.
public static class MemoryGate
{
    static readonly HashSet<Provenance> Trusted = [Provenance.Authoritative, Provenance.Operator];

    public static Admission Admit(MemoryItem candidate, IReadOnlyCollection<MemoryItem> existing)
    {
        var incumbent = existing.FirstOrDefault(m =>
            m.Key.Equals(candidate.Key, StringComparison.OrdinalIgnoreCase) && m.Tier == Tier.Active);

        if (incumbent is { Source: Provenance.Authoritative } &&
            !incumbent.Value.Equals(candidate.Value, StringComparison.OrdinalIgnoreCase) &&
            candidate.Source != Provenance.Authoritative)
            return new Admission(candidate with { Tier = Tier.Rejected },
                $"contradicts the authoritative value '{incumbent.Value}'");

        if (Trusted.Contains(candidate.Source))
            return new Admission(candidate with { Tier = Tier.Active }, $"trusted source ({candidate.Source})");

        // An untrusted source repeating itself is not corroboration - the same web page scraped
        // twice is one claim. Independence is counted by source kind, not by occurrence.
        var independent = existing
            .Where(m => m.Key.Equals(candidate.Key, StringComparison.OrdinalIgnoreCase) &&
                        m.Value.Equals(candidate.Value, StringComparison.OrdinalIgnoreCase) &&
                        m.Source != candidate.Source)
            .Select(m => m.Source)
            .Distinct()
            .Count();

        return independent >= 1
            ? new Admission(candidate with { Tier = Tier.Active, Corroborations = independent + 1 },
                $"corroborated by {independent} independent source(s)")
            : new Admission(candidate with { Tier = Tier.Quarantined },
                $"untrusted source ({candidate.Source}), no independent corroboration");
    }

    /// What the agent is actually allowed to see. Quarantined items are not "included with a
    /// warning" - a caveat in the context window is still content the model will use.
    public static IReadOnlyList<MemoryItem> Retrievable(IEnumerable<MemoryItem> store) =>
        [.. store.Where(m => m.Tier == Tier.Active)];
}
