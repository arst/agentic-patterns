// Self-consistency: sample the SAME prompt from the SAME agent N times at high
// temperature, then majority-vote the final answers. Sampling diversity comes
// from temperature, not from different personas (contrast: Voting.AgentFramework
// uses distinct agents; here it is one agent, one prompt).
// For contrast, a single temperature-0 greedy answer is fetched first — on
// trap problems the greedy path may take the tempting wrong answer
// (here: "96 days" instead of 49), while the vote over sampled paths recovers the truth.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

const int SampleCount = 5;
const float SampleTemperature = 0.9f;

const string Problem =
    "A patch of lily pads doubles in size every day. It takes 48 days to cover " +
    "the whole lake. A second lake is twice as big, and its patch also doubles " +
    "daily, starting from the same initial patch size. The first patch covers its " +
    "lake in 48 days — how many days does the patch need to cover the second lake? " +
    "Give the final answer as a number of days.";

AIAgent reasoner = new ChatClientAgent(
    Settings.ChatClient,
    name: "Reasoner",
    instructions: """
                  Solve the problem step by step, showing your reasoning.
                  Then give the final answer as a bare number (e.g. "42"), nothing else.
                  """);

Console.WriteLine("=== Self-Consistency Sampling ===\n");
Console.WriteLine($"Problem: {Problem}\n");

// Baseline: one greedy (temperature 0) answer.
var greedy = await reasoner.RunAsync<ReasonedAnswer>(
    Problem,
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.0f }));
Console.WriteLine($"Greedy (T=0.0) answer: {greedy.Result.FinalAnswer}\n");

// N independent samples of the same prompt, concurrently. The agent is
// stateless and each RunAsync is a fresh session, so paths are independent.
Console.WriteLine($"Sampling {SampleCount} reasoning paths at T={SampleTemperature}...\n");

var sampleOptions = new ChatClientAgentRunOptions(
    new ChatOptions { Temperature = SampleTemperature });

var samples = await Task.WhenAll(
    Enumerable.Range(1, SampleCount).Select(async i =>
    {
        var response = await reasoner.RunAsync<ReasonedAnswer>(Problem, options: sampleOptions);
        return (Index: i, response.Result);
    }));

foreach (var (index, sample) in samples)
    Console.WriteLine($"Path {index}: {sample.FinalAnswer}\n  {sample.Reasoning}\n");

// Normalize and majority-vote.
var tally = samples
    .GroupBy(s => s.Result.FinalAnswer.Trim().ToLowerInvariant())
    .OrderByDescending(g => g.Count())
    .ToList();

Console.WriteLine("Vote tally:");
foreach (var group in tally)
    Console.WriteLine($"  '{group.Key}': {group.Count()}/{SampleCount}");

var consensus = tally[0];
var disagreementRate = 1.0 - (double)consensus.Count() / SampleCount;

Console.WriteLine($"\nConsensus answer: {consensus.Key} " +
                  $"({consensus.Count()}/{SampleCount} paths, disagreement rate {disagreementRate:P0})");
Console.WriteLine($"Greedy said: {greedy.Result.FinalAnswer.Trim().ToLowerInvariant()} — " +
                  (greedy.Result.FinalAnswer.Trim().ToLowerInvariant() == consensus.Key
                      ? "sampling agreed with greedy this time."
                      : "sampling + voting overturned the greedy answer."));

internal record ReasonedAnswer(string Reasoning, string FinalAnswer);
