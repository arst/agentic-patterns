using LeastToMost.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Least-to-most: decompose into an ordered chain of easier subproblems, then solve them in
// order, each one seeing the ANSWERS to the previous ones.
//
// The difference from chain of thought is where the intermediate results live. CoT keeps them
// inside one generation, where a wrong early step quietly poisons everything after it. Here each
// subproblem is its own call whose input is the previous answers as facts - so the sequence is
// the host's, not the model's.
//
// But note what "established facts" costs: it is a rigid error-propagation channel. A wrong
// figure in step 2 is not questioned by step 5, it is cited by it. Externalising the intermediate
// state does not make the chain safer by itself - it makes it CHECKABLE, which is only a benefit
// if something checks. So the host attaches a deterministic verifier where one exists, and says
// plainly where one does not.
//
// And a verifier that cannot refuse is only telemetry. Once a deterministic check has proved a
// value wrong, that value does not become an established fact for the steps after it, and does
// not become the run's answer either. The run ends CONTESTED instead - a worse-looking output
// and a far better one than a number the host already knows is false.

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

// A deterministic verifier for the one step that has one. The upgrade takes effect at the next
// billing date on or after 1 July, which is 3 July.
var expectedTotal = StepChecks.BillingTotal(
    start: new DateOnly(2025, 3, 3), upgradeEffective: new DateOnly(2025, 7, 3),
    cancelled: new DateOnly(2025, 10, 15), beforeUpgrade: 14m, afterUpgrade: 22m);

var solved = new List<SolvedStep>();
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

    // ── The checkpoint ───────────────────────────────────────────────────────
    // Only the final step has a verifier here, and that is the honest situation: most
    // subproblems in most chains do not. Where one exists, a failed check is caught before the
    // answer becomes an "established fact" that every later step cites.
    var isFinal = step.Order == steps.Count;
    if (!isFinal)
    {
        solved.Add(new SolvedStep(step, answer, StepStatus.Unverified));
        Console.WriteLine($"\n[{step.Order}] {step.Question}\n    → {answer.ReplaceLineEndings(" ")}   [no verifier for this step]");
        continue;
    }

    var check = StepChecks.AgainstTotal(answer, expectedTotal);
    Console.WriteLine($"\n[{step.Order}] {step.Question}\n    → {answer.ReplaceLineEndings(" ")}");
    Console.WriteLine($"    [check] {check.Detail}");

    if (!check.Passed)
    {
        // One retry, with the discrepancy named. Not a loop: an unbounded "try again" on a
        // criterion the model cannot see is how a sample becomes a hang.
        answer = (await solver.RunAsync(
            $"{prompt}\n\nA deterministic check of your answer failed: {check.Detail}. " +
            "Recompute carefully, listing each billing date and its charge.", options: precise)).Text.Trim();

        check = StepChecks.AgainstTotal(answer, expectedTotal);
        Console.WriteLine($"    → retry: {answer.ReplaceLineEndings(" ")}");
        Console.WriteLine($"    [check] {check.Detail}");
    }

    solved.Add(new SolvedStep(step, answer, check.Passed ? StepStatus.Accepted : StepStatus.Contested));

    // A step the host has proved wrong is not handed to the steps after it. Here it is the last
    // step, so there are none - but the rule is the point, not the arithmetic.
    if (!check.Passed) break;
}

var last = solved[^1];
Console.WriteLine(last.Status switch
{
    StepStatus.Contested => $"""

                             === Run stopped: the final answer failed a deterministic check ===
                               candidate:  {last.Answer.ReplaceLineEndings(" ")}
                               host rule:  EUR {expectedTotal:F2}
                               status:     CONTESTED

                             The host will not hand on a value it has already proved wrong. A
                             chain that prints this is working; one that prints the number anyway
                             was only ever logging its verifier.
                             """,
    _ => $"\n=== Final answer (checked against the host's schedule) ===\n{last.Answer}"
});

internal enum StepStatus { Accepted, Unverified, Contested }

internal sealed record SolvedStep(SubProblem Step, string Answer, StepStatus Status);

internal sealed record ProposedSteps(string[] Steps);
