using System.Text.Json;
using MemoryManagement.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

#pragma warning disable MEAI001 // SummarizingChatReducer is marked evaluation-only

// Memory management with Agent Framework GA first-class mechanisms (no hand-rolled history):
// 1. InMemoryChatHistoryProvider — the agent-managed, session-scoped chat history store.
// 2. SummarizingChatReducer      — compacts older turns into a summary once history grows.
// 3. SerializeSessionAsync / DeserializeSessionAsync — persist a session across app restarts.

var sessionFile = Path.Combine(AppContext.BaseDirectory, "session.json");
var longTermFile = Path.Combine(AppContext.BaseDirectory, "long-term-memory.json");
var invocationAgentRuns = 0; // invocation state: this process run only

// The reducer summarizes older messages once the history exceeds ~6 messages,
// keeping the last 4 verbatim. Facts from summarized turns survive in the summary.
ChatClientAgent CreateAgent() => new(Settings.ChatClient,
    new ChatClientAgentOptions
    {
        Name = "ReportAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a helpful assistant. Remember facts the user tells you " +
                           "and use them in later answers. Be concise."
        },
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new SummarizingChatReducer(Settings.ChatClient, targetCount: 4, threshold: 2),
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded
        })
    });

void PrintHistoryCount(AgentSession session, string label)
{
    var count = session.TryGetInMemoryChatHistory(out var messages) ? messages!.Count : 0;
    Console.WriteLine($"  [{label}: {count} messages in agent-managed history]");
}

var agent = CreateAgent();
var session = await agent.CreateSessionAsync();

Console.WriteLine("---- Step 1: Multi-turn conversation — the agent accumulates memory ----\n");

foreach (var input in new[]
         {
             "My name is Anna and I prefer weekly PDF reports, not slides.",
             "Our team demo is every Thursday at 10:00.",
             "Also note: the audience for my reports is the executive board."
         })
{
    Console.WriteLine($"User: {input}");
    invocationAgentRuns++;
    Console.WriteLine($"Agent: {await agent.RunAsync(input, session)}\n");
}

PrintHistoryCount(session, "after 3 turns, reducer applied");

Console.WriteLine("\n---- Step 2: Recall within the session ----\n");

var recall = await agent.RunAsync("Which format do I prefer for reports, and who is the audience?", session);
invocationAgentRuns++;
Console.WriteLine($"Agent: {recall}");

Console.WriteLine("\n---- Step 3: Persist session, simulate app restart, restore ----\n");

var serialized = await agent.SerializeSessionAsync(session);
await File.WriteAllTextAsync(sessionFile, serialized.GetRawText());
Console.WriteLine($"Session saved to {sessionFile}");

// "Restart": brand-new agent instance, session state restored from disk.
var restartedAgent = CreateAgent();
using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sessionFile));
var restoredSession = await restartedAgent.DeserializeSessionAsync(doc.RootElement);
PrintHistoryCount(restoredSession, "restored from disk");

var afterRestart = await restartedAgent.RunAsync(
    "Quick check after restart: what's my name and when is the team demo?", restoredSession);
invocationAgentRuns++;
Console.WriteLine($"Agent: {afterRestart}");

Console.WriteLine("\n---- Step 4: Scoped long-term memory, consent, TTL, and deletion ----\n");
var anna = new MemoryScope("TENANT-ACME", "ANNA");
var otherTenantAnna = new MemoryScope("TENANT-OTHER", "ANNA");
var longTerm = new ScopedLongTermMemory();
Console.WriteLine($"Saved without consent: {longTerm.Remember(anna, "report-format", "weekly PDF", TimeSpan.FromDays(30), consent: false)}");
Console.WriteLine($"Saved with consent: {longTerm.Remember(anna, "report-format", "weekly PDF", TimeSpan.FromDays(30), consent: true)}");
await File.WriteAllTextAsync(longTermFile, longTerm.Serialize());

var restartedLongTerm = ScopedLongTermMemory.Deserialize(await File.ReadAllTextAsync(longTermFile));
Console.WriteLine($"After restart, Anna: {restartedLongTerm.Recall(anna, "report-format")}");
Console.WriteLine($"Same user ID, other tenant: {restartedLongTerm.Recall(otherTenantAnna, "report-format") ?? "not visible"}");
Console.WriteLine($"Deleted memories on request: {restartedLongTerm.Delete(anna)}");

// Business state remains authoritative and is never inferred from conversational memory.
var orders = new Dictionary<string, string> { ["ORD-100"] = "Shipped" };
Console.WriteLine($"Business state from order system: ORD-100 is {orders["ORD-100"]}");
Console.WriteLine($"Invocation state: {invocationAgentRuns} agent runs; not persisted across process runs.");
