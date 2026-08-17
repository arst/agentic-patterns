using LearningAndAdaptation.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shared;

// -----------------------------------------------------------------------------
// Learning & Adaptation pattern
//
// The agent answers three progressively harder questions about the same topic.
// After EACH answer it runs a self-critique step: it reflects on what it did
// well / poorly and calls LearnRule(...) to update its own behavioral policy.
// The PolicyInjectionFilter then prepends those rules to every
// subsequent prompt — so the agent genuinely adapts without being told what to
// do by the user.
// -----------------------------------------------------------------------------

var kernelBuilder = Settings.CreateKernelBuilder();
kernelBuilder.Services.AddSingleton<IPromptRenderFilter, PolicyInjectionFilter>();
kernelBuilder.Services.AddSingleton<IFunctionInvocationFilter, ToolCallLoggingFilter>();
var kernel = kernelBuilder.Build();

var sessionId = Guid.NewGuid().ToString();
kernel.ImportPluginFromObject(new AdaptationTools(sessionId));

// Bind sessionId so the PolicyInjectionFilter can look up rules
var agentKernel = kernel.Clone();
agentKernel.Data["sessionId"] = sessionId;

var answerSettings = new OpenAIPromptExecutionSettings { Temperature = 0.7 };

var critiqueSettings = new OpenAIPromptExecutionSettings
{
    Temperature = 0,
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

// Three increasingly specific questions on the same topic — we want to see the
// agent's style and depth evolve as it accumulates rules about itself.
string[] questions =
{
    "Explain what a transformer neural network is.",
    "Explain how the attention mechanism works inside a transformer.",
    "Explain why positional encoding is necessary in transformers and how it works."
};

for (var i = 0; i < questions.Length; i++)
{
    Console.WriteLine($"\n{'-',60}");
    Console.WriteLine($"  Turn {i + 1} — Question");
    Console.WriteLine($"{'-',60}");
    Console.WriteLine($"  {questions[i]}\n");

    var answer = await agentKernel.InvokePromptAsync(questions[i], new KernelArguments(answerSettings));

    Console.WriteLine($"\n  [answer]\n{answer}");

    var critiquePrompt = $"""
                          You just gave this answer to a user:
                          ---
                          {answer}
                          ---
                          Critically evaluate the answer on three axes:
                            • Clarity  – was it easy to follow?
                            • Depth    – did it actually explain the "why", not just the "what"?
                            • Conciseness – was there any fluff or repetition?

                          If you identify a concrete, actionable improvement you should make in
                          FUTURE answers (not a fix to this one), call LearnRule with a short
                          imperative rule, e.g. "Always lead with a one-sentence summary before diving into detail."

                          You may call LearnRule more than once if you find multiple independent improvements.
                          If the answer was already excellent, do NOT invent fake rules — call nothing.

                          After any tool calls, write a brief critique summary (2–3 sentences).
                          """;

    Console.WriteLine("\n  [self-critique]\n");
    var critique = await agentKernel.InvokePromptAsync(critiquePrompt, new KernelArguments(critiqueSettings));
    Console.WriteLine(critique);
}

Console.WriteLine($"\n{'-',60}");
Console.WriteLine("  Learned policy after 3 turns:");
Console.WriteLine($"{'-',60}");
var rules = PolicyStore.GetRules(sessionId);
if (rules.Count == 0)
    Console.WriteLine("  (no rules were learned — all answers were already great!)");
else
    foreach (var (rule, idx) in rules.Select((r, i) => (r, i + 1)))
        Console.WriteLine($"  {idx}. {rule}");