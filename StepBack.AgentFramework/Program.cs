using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;
using StepBack.AgentFramework;

// Step-back prompting: before answering, ask what general principle the question is an instance
// of - then answer with that principle in hand.
//
// It is one extra call, and it works for the same reason a physics tutor makes you name the
// conservation law before touching the numbers: retrieving the right general rule is an easier
// problem than retrieving the specific answer, and once the rule is on the table the specific
// answer becomes a substitution rather than a recall.

var client = Settings.ChatClient;
var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.1f });

const string Question =
    "A 2.0 kg block is released from rest at the top of a frictionless ramp inclined at 30 " +
    "degrees, 5.0 m along the slope. What is its speed at the bottom, and would a 4.0 kg block " +
    "released the same way be faster, slower, or the same?";

// ── 1. Step back ─────────────────────────────────────────────────────────────
var abstracter = new ChatClientAgent(client, name: "StepBack",
    instructions: """
                  Given a specific question, state the general principle, law, or concept it is
                  an instance of - and nothing else.

                  Do NOT solve the question. Do NOT use any number from it. Two or three
                  sentences naming the governing law and what it implies in general terms.
                  """);

var principle = (await abstracter.RunAsync(Question, options: precise)).Text.Trim();
var leaked = PrincipleGate.LeakedSpecifics(Question, principle);

// One retry with the leak named. If it leaks again the run continues and says so - a leaky
// principle still helps, it just no longer proves the abstraction step did the work.
if (leaked.Count > 0)
{
    Console.WriteLine($"[gate] principle carried the question's specifics ({string.Join(", ", leaked)}); retrying.\n");
    principle = (await abstracter.RunAsync(
        $"{Question}\n\nYour previous attempt used the specific values {string.Join(", ", leaked)}. " +
        "State the principle without any number from the question.", options: precise)).Text.Trim();

    leaked = PrincipleGate.LeakedSpecifics(Question, principle);
    if (leaked.Count > 0)
        Console.WriteLine($"[gate] still leaking {string.Join(", ", leaked)}; continuing anyway.\n");
}

Console.WriteLine($"=== Principle ===\n{principle}\n");

// ── 2. Answer, with the principle supplied ───────────────────────────────────
var solver = new ChatClientAgent(client, name: "Solver",
    instructions: """
                  Answer the question by applying the general principle you are given. Show the
                  substitution briefly, state the numeric answer with units, then answer the
                  comparative part explicitly in terms of the principle.
                  """);

var withPrinciple = await solver.RunAsync(
    $"Principle:\n{principle}\n\nQuestion:\n{Question}", options: precise);

// ── Control: the same model, same temperature, no principle ──────────────────
// Worth printing side by side: on an easy question the two agree and the extra call was waste.
// The pattern earns its keep on questions where the direct answer reaches for the wrong rule.
var direct = await new ChatClientAgent(client, name: "Direct",
        instructions: "Answer the question directly.")
    .RunAsync(Question, options: precise);

Console.WriteLine($"=== Answer via the principle ===\n{withPrinciple}\n");
Console.WriteLine($"=== Direct answer, for comparison ===\n{direct}");
