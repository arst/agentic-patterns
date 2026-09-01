using GraphOfThoughts.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Graph of Thoughts: generate, score, AGGREGATE, refine.
//
// The operation that does not exist in Tree of Thoughts is aggregation. A tree prunes: of two
// good branches you keep one. A graph merges: a node with two parents says "these two partial
// answers are both partly right, combine them". That is the whole reason to pay for the extra
// structure, so this demo is built around a task where two angles genuinely need combining.

var client = Settings.ChatClient;
var creative = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.9f });
var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f });

const string Brief =
    "Write the 'Risks' paragraph of a decision memo recommending that a 40-person B2B SaaS " +
    "company migrate its monolith to microservices over 12 months. Six sentences maximum.";

var generator = new ChatClientAgent(client, name: "Generator",
    instructions: "You draft one focused version of the requested text, from the angle you are " +
                  "given. Stay inside the length limit. No preamble.");

var scorer = new ChatClientAgent(client, name: "Scorer",
    instructions: """
                  Score a candidate paragraph from 0.0 to 1.0 on: concrete risk (not platitudes),
                  relevance to a 40-person company, and whether a decision-maker could act on it.

                  Length is part of the score, not a separate note: the brief allows six sentences.
                  Cap a seven-sentence candidate at 0.6 and a ten-sentence one at 0.3, however good
                  the content is. Use the full range - if everything scores above 0.9 the score is
                  not selecting anything.

                  Return the score and one sentence of justification.
                  """);

var aggregator = new ChatClientAgent(client, name: "Aggregator",
    instructions: "You merge two candidate paragraphs into one. Keep every distinct substantive " +
                  "risk from both, drop the repetition, respect the original length limit.");

var refiner = new ChatClientAgent(client, name: "Refiner",
    instructions: "You tighten a paragraph: same content, sharper language, no new claims, " +
                  "no filler. Respect the original length limit.");

var graph = new ThoughtGraph();
var root = graph.Add("task", Brief, [], 0);

// ── Generate: three angles, in parallel ──────────────────────────────────────
string[] angles =
[
    "organisational risk: team size, on-call load, hiring",
    "technical risk: data consistency, deployment, debugging across services",
    "commercial risk: feature freeze, opportunity cost, customer-visible regressions"
];

var drafts = await Task.WhenAll(angles.Select(async angle =>
{
    var text = (await generator.RunAsync($"{Brief}\n\nAngle: {angle}", options: creative)).Text;
    var score = (await scorer.RunAsync<Score>(text, options: precise)).Result;
    return (Angle: angle, Text: text, score.Value, score.Why);
}));

Console.WriteLine("=== Generated thoughts ===");
var ids = new List<int>();
foreach (var draft in drafts)
{
    var id = graph.Add("draft", draft.Text, [root], draft.Value);
    ids.Add(id);
    Console.WriteLine($"\n[T{id}] score {draft.Value:F2} — {draft.Why}\n{draft.Text}");
}

// ── Aggregate: the two best drafts become ONE node with TWO parents ──────────
var best2 = ids.OrderByDescending(id => graph[id].Score).Take(2).ToList();
var merged = (await aggregator.RunAsync(
    $"{Brief}\n\nCandidate A:\n{graph[best2[0]].Text}\n\nCandidate B:\n{graph[best2[1]].Text}",
    options: precise)).Text;
var mergedScore = (await scorer.RunAsync<Score>(merged, options: precise)).Result;
var mergedId = graph.Add("aggregate", merged, best2, mergedScore.Value);

Console.WriteLine($"\n=== Aggregated T{best2[0]} + T{best2[1]} → T{mergedId} ===");
Console.WriteLine($"score {mergedScore.Value:F2} — {mergedScore.Why}\n{merged}");

// ── Refine: one parent, improve in place ─────────────────────────────────────
var refined = (await refiner.RunAsync(merged, options: precise)).Text;
var refinedScore = (await scorer.RunAsync<Score>(refined, options: precise)).Result;
var refinedId = graph.Add("refine", refined, [mergedId], refinedScore.Value);

Console.WriteLine($"\n=== Refined T{mergedId} → T{refinedId} ===");
Console.WriteLine($"score {refinedScore.Value:F2} — {refinedScore.Why}\n{refined}");

// ── The host picks the winner; refinement is not assumed to be an improvement ──
var winner = graph.Best();
Console.WriteLine($"\n=== Winner: T{winner.Id} ({winner.Kind}, score {winner.Score:F2}) ===");
Console.WriteLine(winner.Text);
Console.WriteLine($"\nDerived from thoughts: {string.Join(", ", graph.Ancestors(winner.Id).Select(a => "T" + a))}");
Console.WriteLine($"\n=== Graph ===\nflowchart LR\n{graph.ToMermaid()}");

internal sealed record Score(double Value, string Why);
