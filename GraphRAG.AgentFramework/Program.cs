using GraphRAG.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// GraphRAG: extract a graph from the corpus first, then answer from the graph.
//
// Plain RAG retrieves the k chunks most similar to the question, which works whenever the answer
// lives in a passage. It cannot answer a question whose answer is not written down anywhere -
// "what are the recurring themes across these incident reports" is a property of the corpus, and
// no chunk contains it. GraphRAG builds the structure that does: entities and relations, grouped
// into communities, summarised once, then queried.
//
// The cost is honest and up front: every document goes through an extraction call before anyone
// asks anything. This pays off on a stable corpus queried many times, and is pure overhead on a
// corpus you read once.

var client = Settings.ChatClient;
var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0f });

// A small corpus of incident reports. The interesting facts span documents - no single report
// mentions both the deploy freeze and the third outage.
(string Id, string Text)[] corpus =
[
    ("INC-101", "The checkout service went down for 22 minutes after the payments gateway began " +
                "returning 503s. Team Atlas owns checkout. The rollback was manual."),
    ("INC-102", "Search latency tripled when the catalog indexer saturated the shared Postgres " +
                "cluster. Team Borealis owns search; the catalog indexer is owned by Team Atlas."),
    ("INC-103", "A failed migration on the shared Postgres cluster took the payments gateway " +
                "offline for 8 minutes. Team Cygnus owns payments."),
    ("INC-104", "Checkout errors spiked again after a deploy from Team Atlas skipped the staging " +
                "environment. The rollback was manual, again."),
    ("INC-105", "The marketing site was unavailable for 3 minutes during a CDN configuration " +
                "change by Team Delta. No other service was affected.")
];

// ── 1. Extract, once per document ────────────────────────────────────────────
var extractor = new ChatClientAgent(client, name: "Extractor",
    instructions: """
                  Extract entities and their relationships from an incident report.

                  Entities are services, teams, infrastructure components, and notable recurring
                  conditions (for example "manual rollback", "skipped staging"). Relationships use
                  short verb types: owns, depends-on, affected, caused-by, deployed-to.

                  Name each entity with the shortest form the text supports - "checkout", not "the
                  checkout service" - and use that same name every time it appears. Entity names
                  are what join documents together; drift between them silently splits the graph.

                  Only relationships the text actually states. No inference.
                  """);

var graph = new KnowledgeGraph();
Console.WriteLine("=== Extraction ===");
foreach (var (id, text) in corpus)
{
    var extracted = (await extractor.RunAsync<Extraction>(text, options: precise)).Result;
    foreach (var edge in extracted.Relations)
        graph.Add(new Relation(edge.From, edge.Type, edge.To, id));

    Console.WriteLine($"  {id}: {extracted.Relations.Length} relation(s)");
}

Console.WriteLine($"\n=== Graph: {graph.Entities.Count} entities, {graph.Relations.Count} relations ===");
foreach (var relation in graph.Relations)
    Console.WriteLine($"  {relation.From} --{relation.Type}--> {relation.To}   [{relation.SourceDoc}]");

// ── 2. Communities, summarised once ──────────────────────────────────────────
var summariser = new ChatClientAgent(client, name: "Summariser",
    instructions: """
                  Summarise a cluster of related infrastructure facts in two sentences: what this
                  cluster is about and what recurs in it.

                  Also return sourceDocumentIds: every incident id that appears in the facts you
                  actually used. Ids only, exactly as written.
                  """);

var communities = graph.Communities();
var summaries = new List<string>();

Console.WriteLine($"\n=== {communities.Count} communities ===");
foreach (var (community, index) in communities.Select((c, i) => (c, i)))
{
    var edges = string.Join("\n", community.Select(r => $"{r.From} {r.Type} {r.To} [{r.SourceDoc}]"));
    var summarised = (await summariser.RunAsync<CommunitySummary>(edges, options: precise)).Result;

    // The model is asked for its sources, and the host checks them against the graph rather than
    // believing them. Without this the provenance chain breaks exactly here: documents carry ids,
    // relations carry ids, and then a free-text summary carries whatever the model happened to
    // retain - after which the final answerer is asked to "cite the incident ids" and can only
    // repeat, or invent, what reached it. An id the summariser names that is not in the community
    // is a fabrication, and it is cheap to catch because the truth is a set the host already has.
    var actual = community.Select(r => r.SourceDoc).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    var claimed = summarised.SourceDocumentIds ?? [];
    var fabricated = claimed.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();

    // Cite what the community actually contains - the host's set, not the model's recollection.
    summaries.Add($"Community {index + 1} [sources: {string.Join(", ", actual)}]: {summarised.Summary}");

    Console.WriteLine($"\n  Community {index + 1} ({community.Count} relations, " +
                      $"{community.SelectMany(r => new[] { r.From, r.To }).Distinct(StringComparer.OrdinalIgnoreCase).Count()} entities)");
    Console.WriteLine($"    {summarised.Summary}");
    Console.WriteLine($"    sources (from the graph): {string.Join(", ", actual)}");
    if (fabricated.Length > 0)
        Console.WriteLine($"    [provenance] summariser also claimed {string.Join(", ", fabricated)} — " +
                          "not in this community, dropped");
}

var answerer = new ChatClientAgent(client, name: "Answerer",
    instructions: "Answer from the supplied graph evidence only. Cite incident ids, and cite ONLY " +
                  "ids that appear in the evidence you were given — the sources are listed with " +
                  "each summary for exactly that purpose.");

// ── 3a. Global question: answered from community summaries ───────────────────
Console.WriteLine("\n=== Global question ===");
Console.WriteLine("Q: What is the recurring systemic problem across these incidents?\n");
Console.WriteLine(await answerer.RunAsync(
    $"Community summaries:\n{string.Join("\n", summaries)}\n\n" +
    "Q: What is the recurring systemic problem across these incidents?", options: precise));

// ── 3b. Local question: answered from a neighbourhood ────────────────────────
var neighbourhood = graph.Neighbourhood("Team Atlas", hops: 2);
Console.WriteLine("\n=== Local question (2-hop neighbourhood of 'Team Atlas') ===");
Console.WriteLine("Q: What is Team Atlas involved in, directly and indirectly?\n");
Console.WriteLine(await answerer.RunAsync(
    $"Evidence:\n{string.Join("\n", neighbourhood.Select(r => $"{r.From} {r.Type} {r.To} [{r.SourceDoc}]"))}\n\n" +
    "Q: What is Team Atlas involved in, directly and indirectly?", options: precise));

internal sealed record CommunitySummary(string Summary, string[] SourceDocumentIds);
internal sealed record ExtractedRelation(string From, string Type, string To);
internal sealed record Extraction(ExtractedRelation[] Relations);
