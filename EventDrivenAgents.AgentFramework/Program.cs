using EventDrivenAgents.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Event-driven agents: no orchestrator, no call graph. Agents subscribe to topics and publish
// what they learn; the wiring is the subscription table.
//
// The trade is real. You get agents that can be added without editing a coordinator, and a bus
// you can point at a real broker later. You give up the ability to read the flow off one page -
// and you take on the failure mode a supervisor cannot have: reaction loops. Hence the budget
// baked into the bus rather than bolted onto one handler.

var client = Settings.ChatClient;
var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f });

var bus = new EventBus(maxEvents: 12, maxGeneration: 4);

var researcher = new ChatClientAgent(client, name: "Researcher",
    instructions: "Given a purchase request, list in three bullets what a buyer would need to " +
                  "check about the vendor and the contract. No preamble.");

var risk = new ChatClientAgent(client, name: "Risk",
    instructions: "Given findings about a purchase, state the single biggest risk and rate it " +
                  "low/medium/high. Two sentences.");

var approver = new ChatClientAgent(client, name: "Approver",
    instructions: "Given a risk assessment for a purchase, decide APPROVE or ESCALATE and give " +
                  "one sentence of reasoning.");

// ── Subscriptions are the architecture ───────────────────────────────────────
bus.Subscribe("PurchaseRequested", async e =>
[
    new AgentEvent("FindingsProduced", (await researcher.RunAsync(e.Payload, options: precise)).Text,
        "Researcher", 0)
]);

bus.Subscribe("FindingsProduced", async e =>
[
    new AgentEvent("RiskAssessed", (await risk.RunAsync(e.Payload, options: precise)).Text, "Risk", 0)
]);

bus.Subscribe("RiskAssessed", async e =>
[
    new AgentEvent("DecisionMade", (await approver.RunAsync(e.Payload, options: precise)).Text,
        "Approver", 0)
]);

// Nothing subscribes to DecisionMade. That makes it a TERMINAL event - the workflow finished -
// which the bus records separately from dead letters. A workflow output filed as a delivery
// failure makes the dead-letter queue useless as an alarm.

bus.Publish(new AgentEvent("PurchaseRequested",
    "Purchase request: 3-year contract with a Norwegian logistics SaaS vendor, EUR 84,000/year, " +
    "requires access to our customer address database.", "Intake", 0));

await bus.RunToCompletionAsync(e =>
    Console.WriteLine($"\n── {e.Topic} (gen {e.Generation}, from {e.Source}) ──\n{e.Payload}"));

Console.WriteLine($"\n=== Done: {bus.Published} events dispatched ===");
foreach (var terminal in bus.TerminalEvents)
    Console.WriteLine($"  terminal: {terminal.Topic} (gen {terminal.Generation}) from {terminal.Source} " +
                      "— nothing subscribes, the workflow ends here");
foreach (var dead in bus.DeadLetters)
    Console.WriteLine($"  dead-letter: {dead.Event.Topic} (gen {dead.Event.Generation}) " +
                      $"from {dead.Event.Source} — {dead.Reason}");

if (bus.DeadLetters.Count == 0)
    Console.WriteLine("  no dead letters: nothing hit the event budget or the generation cap.");
