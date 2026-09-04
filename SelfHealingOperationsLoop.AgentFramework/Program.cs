using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SelfHealingOperationsLoop.AgentFramework;
using Shared;

var before = new ServiceHealth("checkout-v43", 1900, 0.14, "latency rose after checkout-v43 deployed");
var policy = new HealingPolicy(
    MaxP99Milliseconds: 450,
    MaxErrorRate: 0.02,
    AllowedActions: new HashSet<string>(StringComparer.Ordinal) { "rollback_deploy" },
    MinimumConfidence: 0.8);

var diagnostician = new ChatClientAgent(Settings.ChatClient, name: "OperationsDiagnostician",
    instructions: "Diagnose the supplied SLO breach from the evidence. Choose exactly one action: rollback_deploy, restart_service, run_migration, or escalate. Confidence is 0..1. Do not execute anything.");
var diagnosis = (await diagnostician.RunAsync<Diagnosis>($"""
    Current health: {before}
    Recent change: checkout-v43 was deployed five minutes before the regression.
    Previous version checkout-v42 was healthy.
    """, options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0f }))).Result;

var report = new SelfHealingLoop(policy).Run(before, diagnosis, action =>
{
    Console.WriteLine($"Host executes: {action}");
    return new("checkout-v42", 310, 0.006, "baseline restored");
});

foreach (var item in report.Events)
    Console.WriteLine($"{item.Phase,-10} {item.Detail}");
Console.WriteLine($"Result: {report.Status}");
