namespace GraphOfThoughts.AgentFramework;

public sealed record Thought(int Id, string Kind, string Text, IReadOnlyList<int> Parents, double Score);

/// The host owns the reasoning structure; the model only fills nodes in.
///
/// Tree of Thoughts can only branch: every thought has exactly one parent, so two promising
/// lines can never be combined - you pick one and throw the other away. Here a thought may have
/// several parents, which is what makes *aggregation* expressible: "merge these two partial
/// answers into one better answer" is an edge, not a prompt trick.
///
/// A node can only name parents that already exist, so the graph is acyclic by construction -
/// there is no cycle check anywhere, because there is no way to create one.
public sealed class ThoughtGraph
{
    readonly List<Thought> nodes = [];

    public IReadOnlyList<Thought> Nodes => nodes;

    public int Add(string kind, string text, IReadOnlyList<int> parents, double score)
    {
        foreach (var parent in parents)
            if (parent < 0 || parent >= nodes.Count)
                throw new ArgumentOutOfRangeException(nameof(parents),
                    $"Thought {parent} does not exist yet; a thought can only build on earlier ones.");

        nodes.Add(new Thought(nodes.Count, kind, text, parents, score));
        return nodes.Count - 1;
    }

    public Thought this[int id] => nodes[id];

    /// Highest-scoring thought, ties broken towards the later (more derived) one.
    public Thought Best() => nodes.Count == 0
        ? throw new InvalidOperationException("The graph is empty.")
        : nodes.Aggregate((best, next) => next.Score >= best.Score ? next : best);

    /// Every thought this one was derived from, transitively - the provenance of an answer.
    public IReadOnlyList<int> Ancestors(int id)
    {
        var seen = new SortedSet<int>();
        var queue = new Queue<int>(nodes[id].Parents);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            foreach (var parent in nodes[current].Parents) queue.Enqueue(parent);
        }

        return [.. seen];
    }

    public string ToMermaid() => string.Join("\n", nodes.SelectMany(n =>
        n.Parents.Select(p => $"    T{p} --> T{n.Id}[\"{n.Kind} {n.Score:F2}\"]")));
}
