using LeastToMost.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Least-to-most: decompose into an ordered chain of easier subproblems, then solve them in
// order, each one seeing the ANSWERS to the previous ones.
//
// The difference from chain of thought is where the intermediate results live. CoT keeps them
// inside one generation, where a wrong early step quietly poisons everything after it. Here each
// subproblem is its own call whose input is the previous answers as facts - so a step can be
// inspected, and the sequence is the host's, not the model's.

var client = Settings.ChatClient;
var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.1f });

const string Question =
    "Anna subscribed on 3 March 2025 at EUR 14/month, upgraded to EUR 22/month effective " +
    "1 July 2025, and cancelled on 15 October 2025. Billing runs monthly on the 3rd, there is " +
    "no proration, an upgrade takes effect at the next billing date, and cancelling ends the " +
    "period already paid for. How much did Anna pay in total?";

// ── 1. Decompose ─────────────────────────────────────────────────────────────
var decomposer = new ChatClientAgent(client, name: "Decomposer",
    instructions: """
                  Break a problem into an ordered list of simpler subproblems, easiest first,
                  where each one can be answered using only the original problem plus the answers
                  to the subproblems before it.

                  Do not answer them. Do not restate the original question - the host appends it.
                  At most 5 subproblems.
                  """);

var proposed = (await decomposer.RunAsync<ProposedSteps>(Question, options: precise)).Result;
var steps = Decomposition.Normalize(proposed.Steps, Question, max: 6);

Console.WriteLine("=== Decomposition (last step is the original question, guaranteed by the host) ===");
foreach (var step in steps) Console.WriteLine($"  {step.Order}. {step.Question}");

// ── 2. Solve in order, accumulating answers as facts ─────────────────────────
var solver = new ChatClientAgent(client, name: "Solver",
    instructions: """
                  Answer the current subproblem. You are given the original problem and the
                  answers to every earlier subproblem - treat those answers as established facts
                  and do not redo them. Answer in one or two sentences, ending with the value.
                  """);

var solved = new List<(SubProblem Step, string Answer)>();
foreach (var step in steps)
{
    var known = solved.Count == 0
        ? "(none yet)"
        : string.Join("\n", solved.Select(s => $"  Q{s.Step.Order}: {s.Step.Question}\n  A{s.Step.Order}: {s.Answer}"));

    var prompt = $"""
                  Original problem:
                  {Question}

                  Established answers:
                  {known}

                  Subproblem {step.Order}: {step.Question}
                  """;

    // A fresh, sessionless run per subproblem: the only thing carried forward is the answer
    // text the host chose to carry, never the previous call's reasoning.
    var answer = (await solver.RunAsync(prompt, options: precise)).Text.Trim();
    solved.Add((step, answer));

    Console.WriteLine($"\n[{step.Order}] {step.Question}\n    → {answer.ReplaceLineEndings(" ")}");
}

Console.WriteLine($"\n=== Final answer ===\n{solved[^1].Answer}");

internal sealed record ProposedSteps(string[] Steps);
