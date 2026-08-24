using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Shared;

var chatClient = Settings.ChatClient;
var chatConfig = new ChatConfiguration(chatClient);

var policyTool = AIFunctionFactory.Create((string topic) => topic.ToLowerInvariant() switch
{
    "warranty" => "Two-year limited warranty.",
    "returns" => "30-day return window with order number.",
    _ => "No policy found."
}, "GetSupportPolicy", "Get the authoritative TechCorp warranty or returns policy.");

var warrantyTool = AIFunctionFactory.Create((string serial) => $"Serial {serial}: in warranty until 2027.",
    "CheckWarrantyStatus", "Check warranty status for a laptop serial number.");

// Deliberate distractor: irrelevant to support policy questions. A good trajectory ignores it.
var storesTool = AIFunctionFactory.Create(() => "Stores in Berlin, Oslo, Lisbon.",
    "GetStoreLocations", "List TechCorp retail store locations.");

AITool[] tools = [policyTool, warrantyTool, storesTool];
var agent = new ChatClientAgent(chatClient, name: "SupportAgent",
    instructions: "You are a TechCorp support agent. Use tools when needed. Answer concisely.",
    tools: tools);

string[] queries =
[
    "What warranty do TechCorp laptops come with?",
    "Is my laptop serial TC-9931 still under warranty?"
];

Console.WriteLine("==== Trajectory evaluation ====\n");
foreach (var query in queries)
{
    var runResponse = await agent.RunAsync(query);

    // The run response carries the full trajectory: assistant tool-call messages, tool
    // results, and the final answer. Prepend the user turn to form the conversation the
    // agent evaluators score.
    IList<ChatMessage> messages = [new(ChatRole.User, query), .. runResponse.Messages];
    var response = new ChatResponse([.. runResponse.Messages]);

    Console.WriteLine($"Q: {query}\nA: {runResponse.Text}");
    foreach (var line in await ScoreAsync(messages, response))
        Console.WriteLine($"   {line}");
    Console.WriteLine();
}

async Task<List<string>> ScoreAsync(IList<ChatMessage> messages, ChatResponse response)
{
    var evaluators = new (string Name, IEvaluator Eval, EvaluationContext Ctx, string Metric)[]
    {
        ("ToolCallAccuracy", new ToolCallAccuracyEvaluator(),
            new ToolCallAccuracyEvaluatorContext(tools), ToolCallAccuracyEvaluator.ToolCallAccuracyMetricName),
        ("TaskAdherence", new TaskAdherenceEvaluator(),
            new TaskAdherenceEvaluatorContext(tools), TaskAdherenceEvaluator.TaskAdherenceMetricName),
        ("IntentResolution", new IntentResolutionEvaluator(),
            new IntentResolutionEvaluatorContext(tools), IntentResolutionEvaluator.IntentResolutionMetricName)
    };

    var lines = new List<string>();
    foreach (var e in evaluators)
    {
        var result = await e.Eval.EvaluateAsync(messages, response, chatConfig, [e.Ctx]);
        var metric = result.Get<EvaluationMetric>(e.Metric);
        var value = metric switch
        {
            BooleanMetric b => b.Value?.ToString() ?? "n/a",
            NumericMetric n => n.Value?.ToString() ?? "n/a",
            _ => "?"
        };
        lines.Add($"{e.Name,-18}: {value} — {metric.Reason}");
    }
    return lines;
}
