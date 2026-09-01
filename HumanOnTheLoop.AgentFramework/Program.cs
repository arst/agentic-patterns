using HumanOnTheLoop.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Human-on-the-loop: the agent works at its own pace and narrates; the human watches and can cut
// in. Approval is the exception, not the rhythm.
//
// The whole pattern is one design decision - what happens when the human says nothing - and it
// is answered per action, not per agent: reversible actions proceed on silence, irreversible ones
// stop and wait. Get that split wrong in the safe direction and you have rebuilt
// HumanInTheLoop with extra steps; wrong in the other and you have an agent that deletes a
// production database because nobody was reading the terminal.

var client = Settings.ChatClient;
var watcher = new InterruptWatcher();
var window = TimeSpan.FromSeconds(3);

var agent = new ChatClientAgent(client, name: "Operator",
    instructions: "You are an infrastructure assistant. Given a task and the log of what has " +
                  "been done, describe in one sentence what you are doing now. No lists.");

// The plan the agent works through. In a real system these come from the agent; what matters
// here is that the reversibility flag is the HOST's classification of the action, never the
// model's claim about it.
ProposedAction[] plan =
[
    new("scale_up", "Scale the api deployment from 3 to 6 replicas", Reversible: true),
    new("rotate_logs", "Archive and rotate logs older than 14 days", Reversible: true),
    new("drop_index", "Drop the unused idx_orders_legacy index on the primary database", Reversible: false),
    new("purge_cache", "Flush the CDN cache for /assets/*", Reversible: true)
];

Console.WriteLine($"""
                   Agent is running autonomously. It pauses {window.TotalSeconds:F0}s before each action.
                   Type anything and press Enter during a pause to interrupt.
                   Irreversible actions wait for an explicit 'ok' regardless.

                   """);

var done = new List<string>();

foreach (var action in plan)
{
    var narration = (await agent.RunAsync(
        $"Task: routine maintenance window.\nDone so far: {(done.Count == 0 ? "nothing" : string.Join("; ", done))}\n" +
        $"Now: {action.Detail}",
        options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.3f }))).Text.Trim();

    Console.WriteLine($"── {action.Name} {(action.Reversible ? "" : "[IRREVERSIBLE] ")}──");
    Console.WriteLine($"   {narration}");
    Console.Write(action.Reversible
        ? $"   proceeding in {window.TotalSeconds:F0}s unless you object... "
        : "   irreversible — type 'ok' to allow, anything else to skip: ");

    var typed = await watcher.WatchAsync(action.Reversible ? window : TimeSpan.FromSeconds(15));
    var acknowledged = string.Equals(typed?.Trim(), "ok", StringComparison.OrdinalIgnoreCase);
    var interrupted = typed is not null && !acknowledged;

    switch (OversightPolicy.Decide(action, interrupted, acknowledged))
    {
        case Oversight.Proceed:
            Console.WriteLine("done.\n");
            done.Add(action.Name);
            break;

        case Oversight.Halted:
            Console.WriteLine($"\n   HALTED by operator: \"{typed}\"\n");
            Console.WriteLine($"=== Stopped after {done.Count} action(s): {string.Join(", ", done)} ===");
            return;

        case Oversight.AwaitingAck:
            // No ack inside the window is a NO. The run continues; the action does not.
            Console.WriteLine("\n   skipped — no acknowledgement.\n");
            break;
    }
}

Console.WriteLine($"=== Completed: {string.Join(", ", done)} ===");
