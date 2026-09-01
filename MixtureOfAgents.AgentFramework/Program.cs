using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MixtureOfAgents.AgentFramework;
using Shared;

// Mixture of Agents: layered proposers. Layer 1 answers cold; layer 2 answers again, this time
// having read every layer-1 answer; a final aggregator writes the answer that ships.
//
// This is not voting. Voting picks one of N answers and throws away N-1; the losers contribute
// nothing even when they were right about one thing. In a mixture, layer 2 *reads* the losers -
// a weak proposal that happens to raise the one risk everyone else missed still reaches the
// final answer. The cost is honest: 2 layers x 3 agents + 1 aggregator is 7 calls for one answer.

var client = Settings.ChatClient;

const string Question =
    "We run a 30-person consultancy on a self-hosted GitLab instance that one part-time admin " +
    "maintains. Should we migrate to a managed SaaS plan? Give a recommendation with reasoning, " +
    "under 200 words.";

// ── Layer 1: propose, independently ──────────────────────────────────────────
// Different temperatures and framings, so the layer explores rather than agreeing three times.
(string Name, string Instructions, float Temperature)[] proposers =
[
    ("Pragmatist", "You answer from operational reality: who does the work, what breaks at 3am.", 0.4f),
    ("Economist", "You answer from total cost of ownership, including staff time and risk.", 0.7f),
    ("Contrarian", "You argue the less obvious side seriously, without being perverse.", 0.9f)
];

var layer1 = await Task.WhenAll(proposers.Select(async p =>
{
    var agent = new ChatClientAgent(client, name: p.Name, instructions: p.Instructions);
    var options = new ChatClientAgentRunOptions(new ChatOptions { Temperature = p.Temperature });
    return new Proposal(p.Name, (await agent.RunAsync(Question, options: options)).Text);
}));

var round1 = new ProposalSet(layer1);
Console.WriteLine("=== Layer 1 ===");
foreach (var proposal in layer1)
    Console.WriteLine($"\n[{proposal.Author}]\n{proposal.Text}");

// ── Layer 2: refine, having read layer 1 ─────────────────────────────────────
// Same question, but each refiner sees all three earlier proposals - anonymised, and in its own
// rotation so the layer does not inherit one shared position bias.
var refiner = new ChatClientAgent(client, name: "Refiner",
    instructions: """
                  You are given a question and several independent proposed answers.

                  Write a better answer than any of them. Keep what is correct, correct what is
                  wrong, and resolve the disagreements explicitly rather than averaging them.
                  Never refer to "the proposals" - write the answer itself. Under 200 words.
                  """);

var medium = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.5f });
var layer2 = await Task.WhenAll(Enumerable.Range(0, round1.Count).Select(async i =>
    new Proposal($"Refiner{i + 1}",
        (await refiner.RunAsync($"Question:\n{Question}\n\n{round1.Format(i)}", options: medium)).Text)));

var round2 = new ProposalSet(layer2);
Console.WriteLine("\n=== Layer 2 (each refiner read all of layer 1, in its own ordering) ===");
foreach (var proposal in layer2)
    Console.WriteLine($"\n[{proposal.Author}]\n{proposal.Text}");

// ── Aggregate ────────────────────────────────────────────────────────────────
var aggregator = new ChatClientAgent(client, name: "Aggregator",
    instructions: """
                  You are given a question and several refined answers that already converged
                  somewhat. Produce the single answer to ship.

                  Where they still disagree, pick a side and say why in one clause - do not hedge
                  into a "it depends" that helps nobody. Under 200 words, ending with a one-line
                  recommendation.
                  """);

var final = await aggregator.RunAsync(
    $"Question:\n{Question}\n\n{round2.Format(0)}",
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f }));

Console.WriteLine($"\n=== Final ===\n{final}");
