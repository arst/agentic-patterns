using LearningAndAdaptation.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Shared;

// -----------------------------------------------------------------------------
// Learning & Adaptation pattern — Microsoft Agent Framework
//
// Each turn runs a two-step workflow:
//   AnswerExecutor  ? answers the question, injecting any already-learned rules
//   CritiqueExecutor ? self-critiques the answer and persists new rules to
//                      PolicyStore via structured JSON output
//
// Because PolicyStore is shared across runs, each turn's AnswerExecutor
// automatically picks up every rule discovered in all previous turns —
// so the agent genuinely adapts without any user intervention.
// -----------------------------------------------------------------------------

var chatClient = Settings.ChatClient;

var answerAgent = new ChatClientAgent(chatClient,
    name: "answerer",
    instructions: "You are a clear, precise technical educator. Answer the user's question thoroughly.");

var critiqueAgent = new ChatClientAgent(chatClient,
    name: "critiquer",
    instructions:
    "You are a strict self-evaluator. Critique responses honestly and extract only genuinely useful improvement rules.");

var answerExec = new AnswerExecutor(answerAgent);
var critiqueExec = new CritiqueExecutor(critiqueAgent);

var workflow = new WorkflowBuilder(answerExec)
    .AddEdge(answerExec, critiqueExec)
    .WithOutputFrom(critiqueExec)
    .Build();

var sessionId = Guid.NewGuid().ToString("N")[..8];

string[] questions =
[
    "Explain what a transformer neural network is.",
    "Explain how the attention mechanism works inside a transformer.",
    "Explain why positional encoding is necessary in transformers and how it works."
];

for (var i = 0; i < questions.Length; i++)
{
    Console.WriteLine($"\n{"-",60}");
    Console.WriteLine($"  Turn {i + 1} — {questions[i]}");
    Console.WriteLine($"{"-",60}");

    var rulesBeforeTurn = PolicyStore.GetRules(sessionId);
    if (rulesBeforeTurn.Count > 0)
    {
        Console.WriteLine("\n  [injected policy]");
        foreach (var (r, idx) in rulesBeforeTurn.Select((r, j) => (r, j + 1)))
            Console.WriteLine($"    {idx}. {r}");
    }

    await using var run = await InProcessExecution.RunStreamingAsync(
        workflow, new TurnInput(sessionId, questions[i]));

    await foreach (var evt in run.WatchStreamAsync())
        switch (evt)
        {
            case ExecutorCompletedEvent { ExecutorId: "answer" } completed:
                if (completed.Data is AnswerPayload ap) Console.WriteLine($"\n  [answer]\n{ap.Answer}");
                break;

            case WorkflowOutputEvent output:
                if (output.Data is LearnedRules lr && lr.Rules.Count > 0)
                {
                    Console.WriteLine("\n  [rules learned this turn]");
                    foreach (var rule in lr.Rules)
                        Console.WriteLine($"    • {rule}");
                }
                else
                {
                    Console.WriteLine("\n  [critique: no new rules — answer was already good]");
                }

                break;
        }
}

Console.WriteLine($"\n{"-",60}");
Console.WriteLine("  Final learned policy:");
Console.WriteLine($"{"-",60}");
var finalRules = PolicyStore.GetRules(sessionId);
if (finalRules.Count == 0)
    Console.WriteLine("  (no rules accumulated)");
else
    foreach (var (rule, idx) in finalRules.Select((r, i) => (r, i + 1)))
        Console.WriteLine($"  {idx}. {rule}");