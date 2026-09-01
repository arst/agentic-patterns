namespace GraphRAG.AgentFramework;

public sealed record Relation(string From, string Type, string To, string SourceDoc);

/// The graph plus the two things GraphRAG needs from it: neighbourhoods for local questions and
/// communities for global ones.
public sealed class KnowledgeGraph
{
    readonly List<Relation> relations = [];

    public IReadOnlyList<Relation> Relations => relations;

    public void Add(Relation relation)
    {
        // Same edge from two documents is corroboration, not a second edge.
        if (relations.Any(r => Same(r, relation))) return;
        relations.Add(relation);
    }

    public IReadOnlyList<string> Entities =>
        [.. relations.SelectMany(r => new[] { r.From, r.To }).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.Ordinal)];

    /// Everything within `hops` of an entity - the evidence for a LOCAL question ("what do we
    /// know about X"), which vector retrieval answers well too.
    public IReadOnlyList<Relation> Neighbourhood(string entity, int hops)
    {
        var frontier = new HashSet<string>([entity], StringComparer.OrdinalIgnoreCase);
        var found = new List<Relation>();

        for (var hop = 0; hop < hops; hop++)
        {
            var edges = relations.Where(r =>
                (frontier.Contains(r.From) || frontier.Contains(r.To)) && !found.Contains(r)).ToList();

            found.AddRange(edges);
            foreach (var edge in edges)
            {
                frontier.Add(edge.From);
                frontier.Add(edge.To);
            }
        }

        return found;
    }

    /// Connected components. This is the part vector retrieval structurally cannot do: "which
    /// clusters exist in this corpus" is a question about the shape of the whole graph, and no
    /// amount of top-k similarity over chunks recovers it - there is no chunk that says it.
    ///
    /// ponytail: components, not Leiden. It is deterministic, needs no parameters, and separates
    /// this corpus correctly. Swap in a real community algorithm when one giant component forms,
    /// which is what happens on any corpus big enough to matter.
    public IReadOnlyList<IReadOnlyList<Relation>> Communities()
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Find(string x)
        {
            parent.TryAdd(x, x);
            return parent[x] == x ? x : parent[x] = Find(parent[x]);
        }

        foreach (var relation in relations) parent[Find(relation.From)] = Find(relation.To);

        return [.. relations
            .GroupBy(r => Find(r.From), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(IReadOnlyList<Relation> (g) => [.. g])];
    }

    static bool Same(Relation a, Relation b) =>
        a.From.Equals(b.From, StringComparison.OrdinalIgnoreCase) &&
        a.To.Equals(b.To, StringComparison.OrdinalIgnoreCase) &&
        a.Type.Equals(b.Type, StringComparison.OrdinalIgnoreCase);
}
