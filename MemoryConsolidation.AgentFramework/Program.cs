using MemoryConsolidation.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Memory consolidation: episodes accumulate, then periodically become facts.
//
// MemoryManagement covers where memory lives. This covers what happens to it over time, which is
// the difference between an agent with a long history and an agent that has learned anything: raw
// episodes are retrieved by recency+importance+relevance and are individually cheap, but a
// thousand of them is a store you cannot afford to search or to read. Consolidation collapses a
// topic's episodes into one semantic memory - a real information loss for the ACTIVE set, taken
// deliberately, because "the customer's exports are slow every month-end" is worth more than
// twelve timestamps. The sources are archived rather than deleted, so the fact keeps a derivation
// and a wrong summary stays correctable.

var client = Settings.ChatClient;
var now = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

// A few weeks in the life of a support agent. Importance is scored at write time - here by the
// host, in a real system usually by a cheap model call.
var episodes = new List<Episode>
{
    new("ep-01", "Customer reported CSV export timing out at month-end.", now.AddDays(-28), 0.6, "exports"),
    new("ep-02", "Customer reported CSV export timing out again, 40k rows.", now.AddDays(-21), 0.6, "exports"),
    new("ep-03", "Advised customer to filter the export by date range.", now.AddDays(-21), 0.3, "exports"),
    new("ep-04", "Customer reported CSV export timeout, month-end again.", now.AddDays(-1), 0.7, "exports"),
    new("ep-05", "Customer asked whether an API export exists.", now.AddHours(-3), 0.5, "exports"),

    new("ep-06", "Customer's payment failed; card expired.", now.AddDays(-45), 0.8, "billing"),
    new("ep-07", "Customer updated card; payment retried successfully.", now.AddDays(-45), 0.4, "billing"),

    new("ep-08", "Customer mentioned they are evaluating a competitor.", now.AddDays(-9), 0.9, "renewal")
};

// ── Retrieval: what the agent would pull for a specific question ─────────────
const string Query = "The customer is asking about exports timing out. What do I know?";
var scored = EpisodicRetrieval.Score(episodes, Query, now);

Console.WriteLine("=== Episodic retrieval (recency + importance + relevance) ===");
foreach (var item in scored.Take(5))
    Console.WriteLine($"  {item.Total:F2} = rec {item.Recency:F2} + imp {item.Episode.Importance:F2} + " +
                      $"rel {item.Relevance:F2}  |  {item.Episode.Text}");

Console.WriteLine($"\n  (note the 45-day-old billing episode scoring {scored.First(s => s.Episode.Topic == "billing").Total:F2} " +
                  "— important once, not relevant now)");

// ── Consolidation: topics with enough history become semantic memories ───────
var consolidator = new ChatClientAgent(client, name: "Consolidator",
    instructions: """
                  You turn a list of dated episodes about one topic into a single durable fact.

                  Write what is generally true, including any pattern in timing or cause. One or
                  two sentences. Do not list the episodes back. Do not invent causes the episodes
                  do not support.
                  """);

var semantic = new List<SemanticMemory>();
var ripe = Consolidation.Ripe(episodes, minimum: 3);

Console.WriteLine($"\n=== Consolidation: {ripe.Count} topic(s) ripe (>= 3 episodes) ===");
foreach (var group in ripe)
{
    var dated = string.Join("\n", group.OrderBy(e => e.At)
        .Select(e => $"{e.At:yyyy-MM-dd}: {e.Text}"));

    var fact = (await consolidator.RunAsync($"Topic: {group.Key}\n{dated}",
        options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f }))).Text.Trim();

    var sourceIds = group.Select(e => e.Id).Order().ToArray();
    semantic.Add(new SemanticMemory(fact, group.Key, sourceIds, now));
    Console.WriteLine($"\n  [{group.Key}] {sourceIds.Length} episodes -> 1 semantic memory");
    Console.WriteLine($"    {fact}");
    Console.WriteLine($"    derived from: {string.Join(", ", sourceIds)}");

    // ARCHIVED, not deleted. Consolidation removes episodes from the hot retrieval set - that is
    // the lossy step, and the reason it runs on a threshold. It must not remove them from durable
    // history: the semantic memory is model-written prose, and if it is subtly wrong, deleting its
    // sources makes the error canonical and unfalsifiable forever.
    for (var i = 0; i < episodes.Count; i++)
        if (episodes[i].Topic.Equals(group.Key, StringComparison.OrdinalIgnoreCase))
            episodes[i] = episodes[i] with { Status = EpisodeStatus.Archived };
}

var active = episodes.Where(e => e.Status == EpisodeStatus.Active).ToList();
var archived = episodes.Count - active.Count;
Console.WriteLine($"\nActive retrieval set: {active.Count} episodes + {semantic.Count} semantic memories.");
Console.WriteLine($"Archived, still on disk and still auditable: {archived} episodes. " +
                  "Nothing was deleted - a consolidated fact can be checked against its sources.");

// ── The agent answers from the consolidated store ────────────────────────────
var agent = new ChatClientAgent(client, name: "Support",
    instructions: $"""
                   You are a support agent. What you know about this customer:

                   Facts:
                   {string.Join("\n", semantic.Select(m => $"  - {m.Text}"))}

                   Recent episodes:
                   {string.Join("\n", active.OrderByDescending(e => e.At).Select(e => $"  - {e.At:yyyy-MM-dd}: {e.Text}"))}

                   Answer from that. Be specific about what you already know.
                   """);

Console.WriteLine($"\n=== Answer ===");
Console.WriteLine(await agent.RunAsync(
    "The customer is on the phone about export timeouts again. What should I say?",
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.3f })));
