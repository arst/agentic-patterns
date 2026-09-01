namespace MixtureOfAgents.AgentFramework;

public sealed record Proposal(string Author, string Text);

/// The layer-1 outputs, prepared for a layer-2 agent to read.
///
/// Two deliberate distortions, both the host's job rather than the prompt's:
///
///  - **Anonymised.** Refiners see "Proposal A", never "the Optimist said". Author labels invite
///    a refiner to reason about who is usually right instead of about the content, and in a
///    mixture the authors are the same base model wearing different hats anyway.
///  - **Rotated.** Each refiner gets the same proposals in a different order. LLMs weight
///    earlier items more heavily; if every refiner reads the same ordering, that bias is
///    identical across the layer and survives into the aggregate instead of cancelling out.
public sealed class ProposalSet
{
    readonly List<Proposal> proposals;

    public ProposalSet(IEnumerable<Proposal> proposals)
    {
        this.proposals = [.. proposals.Where(p => !string.IsNullOrWhiteSpace(p.Text))];
        if (this.proposals.Count == 0)
            throw new ArgumentException("A layer produced no usable proposals.", nameof(proposals));
    }

    public int Count => proposals.Count;

    /// The proposals as reader `readerIndex` should see them: rotated by that index, anonymised.
    public IReadOnlyList<Proposal> For(int readerIndex) =>
        [.. Enumerable.Range(0, proposals.Count)
            .Select(i => proposals[(i + readerIndex) % proposals.Count])];

    public string Format(int readerIndex) =>
        string.Join("\n\n", For(readerIndex).Select((p, i) =>
            $"Proposal {(char)('A' + i)}:\n{p.Text}"));
}
