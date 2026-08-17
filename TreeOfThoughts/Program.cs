// Tree-of-Thoughts: instead of one linear chain of reasoning, explore several
// candidate "thoughts" per step, score each partial path with an evaluator,
// prune dead ends, and only expand the most promising branches (beam search).
// Demo task: Game of 24 — combine 4 numbers with + - * / to reach exactly 24.
// Branching visibly helps here: most first moves are dead ends.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

const int MaxDepth = 3; // 4 numbers -> 3 combining operations
const int Breadth = 3; // candidate thoughts generated per expansion
const int BeamWidth = 2; // best paths kept per level

const string Task = "Use the numbers 4, 9, 10 and 13, each exactly once, " +
                    "with + - * / and parentheses, to make exactly 24.";

AIAgent generator = new ChatClientAgent(
    Settings.ChatClient,
    name: "ThoughtGenerator",
    instructions: """
                  You are solving a Game of 24 puzzle step by step.
                  A step combines exactly two of the remaining numbers with one operation,
                  producing a new intermediate number.
                  Given the puzzle and the steps taken so far, propose distinct candidate
                  next steps. Each thought must be one line in the form:
                  "a op b = c (remaining: x, y, ...)"
                  If only one number remains and it is 24, the thought is "done: <full expression> = 24".
                  Propose genuinely different steps — do not repeat the same combination.
                  """);

AIAgent evaluator = new ChatClientAgent(
    Settings.ChatClient,
    name: "ThoughtEvaluator",
    instructions: """
                  You judge partial solutions of a Game of 24 puzzle.
                  Given the steps taken so far, check the arithmetic and decide whether the
                  remaining numbers can still reach exactly 24.
                  Score 0.0-1.0 (1.0 = solved or clearly on track).
                  Verdict must be exactly one of: sure, maybe, impossible.
                  "sure" = solved or certainly solvable; "impossible" = arithmetic is wrong
                  or 24 is no longer reachable; otherwise "maybe".
                  """);

// A bit of temperature so the generator's branches actually diverge.
var generatorOptions = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.8f });
var evaluatorOptions = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.0f });

Console.WriteLine("=== Tree of Thoughts (beam search) ===\n");
Console.WriteLine($"Task: {Task}");
Console.WriteLine($"depth={MaxDepth}, breadth={Breadth}, beam={BeamWidth}\n");

var beam = new List<ScoredPath> { new([], 0.0) };
ScoredPath? solved = null;

for (var depth = 1; depth <= MaxDepth && solved is null; depth++)
{
    Console.WriteLine($"Level {depth}:");
    var candidates = new List<ScoredPath>();

    foreach (var path in beam)
    {
        var pathText = path.Steps.Count == 0
            ? "(no steps yet)"
            : string.Join("\n", path.Steps);

        var genResponse = await generator.RunAsync<CandidateThoughts>(
            $"""
             Puzzle: {Task}

             Steps so far:
             {pathText}

             Propose {Breadth} candidate next steps.
             """,
            options: generatorOptions);

        foreach (var thought in genResponse.Result.Thoughts.Take(Breadth))
        {
            var newSteps = path.Steps.Append(thought).ToList();

            var evalResponse = await evaluator.RunAsync<ThoughtEvaluation>(
                $"""
                 Puzzle: {Task}

                 Steps so far:
                 {string.Join("\n", newSteps)}
                 """,
                options: evaluatorOptions);
            var eval = evalResponse.Result;

            var pruned = eval.Verdict.Equals("impossible", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(
                $"  {(pruned ? "x" : "+")} [{eval.Score:F2} {eval.Verdict}] {thought}");

            if (!pruned)
                candidates.Add(new ScoredPath(newSteps, eval.Score));
        }
    }

    if (candidates.Count == 0)
    {
        Console.WriteLine("  All branches pruned — no solution found.");
        break;
    }

    beam = candidates.OrderByDescending(c => c.Score).Take(BeamWidth).ToList();
    Console.WriteLine($"  Beam kept: {string.Join(" | ", beam.Select(b => $"{b.Score:F2} {b.Steps[^1]}"))}\n");

    solved = beam.FirstOrDefault(b =>
        b.Steps[^1].StartsWith("done", StringComparison.OrdinalIgnoreCase));
}

Console.WriteLine("=== Best path ===");
var best = solved ?? beam.OrderByDescending(b => b.Score).FirstOrDefault();
if (best is null || best.Steps.Count == 0)
{
    Console.WriteLine("No viable path survived.");
}
else
{
    foreach (var (step, i) in best.Steps.Select((s, i) => (s, i)))
        Console.WriteLine($"{new string(' ', i * 2)}-> {step}");
    Console.WriteLine($"\nFinal score: {best.Score:F2}" +
                      (solved is not null ? " (solved)" : " (best partial path)"));
}

internal record CandidateThoughts(List<string> Thoughts);

internal record ThoughtEvaluation(double Score, string Verdict);

internal record ScoredPath(List<string> Steps, double Score);
