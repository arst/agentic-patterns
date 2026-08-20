using System.Text.Json;
using Microsoft.Agents.AI;
using OrchestratorWorkers.AgentFramework;
using Shared;

var client = Settings.ChatClient;
var orchestrator = new ChatClientAgent(client,
    """
    Decompose the request into at most 6 independent research tasks. Choose only from these fixed workers:
    MarketResearcher, CompetitorResearcher, RegulatoryResearcher. Use short unique task IDs and concise instructions.
    """, "Orchestrator");

var request = "Assess whether a German launch of a Nordic coffee-subscription business is attractive.";
var plan = (await orchestrator.RunAsync<WorkPlan>(request)).Result;

var registry = new WorkerRegistry();
registry.Register("MarketResearcher", Run("Estimate market demand, growth, and customer segments."));
registry.Register("CompetitorResearcher", Run("Identify direct and indirect competitors and differentiation."));
registry.Register("RegulatoryResearcher", Run("Identify German consumer, subscription, and food-commerce constraints."));

var errors = PlanValidator.Validate(plan, registry.Roles);
if (errors.Count > 0)
    throw new InvalidOperationException("Invalid work plan:\n- " + string.Join("\n- ", errors));

Console.WriteLine("=== Validated dynamic work plan ===");
Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));

var results = await registry.ExecuteAsync(plan, maximumConcurrency: 2);
foreach (var result in results)
    Console.WriteLine($"\n[{result.TaskId}/{result.Worker}] {(result.Succeeded ? result.Output : "FAILED: " + result.Error)}");

var evidence = WorkerRegistry.BuildSynthesisInput(results);
var synthesizer = new ChatClientAgent(client,
    "Synthesize the successful worker reports into a concise recommendation.",
    "Synthesizer");
Console.WriteLine($"\n=== Synthesis ===\n{await synthesizer.RunAsync($"Request: {request}\n\nWorker reports:\n{evidence}")}");
return;

Func<WorkerTask, CancellationToken, Task<string>> Run(string roleInstruction) => async (task, cancellationToken) =>
{
    var worker = new ChatClientAgent(client, roleInstruction, task.Worker);
    return (await worker.RunAsync(task.Instruction, cancellationToken: cancellationToken)).Text;
};
