using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Shared;

#pragma warning disable MEAI001, MAAI001 // compaction pipeline is marked evaluation-only

// Context compaction with the GA compaction pipeline (Microsoft.Agents.AI.Compaction):
// The full conversation stays in the agent-managed history (audit record), while a
// CompactionProvider — an AIContextProvider — rewrites what is SENT TO THE MODEL each turn:
// 1. ToolResultCompactionStrategy  — evicts bulky old tool outputs, keeping a one-line stub.
// 2. SummarizationCompactionStrategy — folds older turns into a rolling "[Summary]" message.
// Both are chained with PipelineCompactionStrategy and fire via CompactionTriggers.
// Contrast with MemoryManagement.AgentFramework, where a SummarizingChatReducer rewrites
// the stored history itself.

var meter = new ModelInputMeter(Settings.ChatClient);

var compaction = new PipelineCompactionStrategy(
[
    // Old tool results are the biggest context hogs — drop them first (keep last 2 groups intact).
    new ToolResultCompactionStrategy(CompactionTriggers.MessagesExceed(8), minimumPreservedGroups: 2),
    // Then fold everything but the last 3 message groups into a rolling summary.
    new SummarizationCompactionStrategy(Settings.ChatClient, CompactionTriggers.MessagesExceed(10), minimumPreservedGroups: 3)
]);

var agent = new ChatClientAgent(meter, new ChatClientAgentOptions
{
    Name = "OrderAgent",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are an order support assistant. Use tools to look up orders. " +
                       "Remember facts the customer tells you. Be concise (one or two sentences).",
        Tools = [AIFunctionFactory.Create(GetOrderStatus), AIFunctionFactory.Create(GetShippingOptions)]
    },
    ChatHistoryProvider = new InMemoryChatHistoryProvider(),
    AIContextProviders = [new CompactionProvider(compaction)]
});

var session = await agent.CreateSessionAsync();

Console.WriteLine("---- A long-running support conversation (compaction trips after ~3 turns) ----\n");

foreach (var input in new[]
         {
             "Hi, my name is Priya, customer id C-1042.", // the fact that must survive compaction
             "What's the status of order A-101?",
             "And order A-102?",
             "Which shipping options do you offer to Denmark?",
             "Please also check order A-103.",
             "Is A-103 slower than A-101 was?",
             "One more: status of order A-104?",
             "Thanks. Summarize which of my orders are still in transit."
         })
{
    Console.WriteLine($"User: {input}");
    Console.WriteLine($"Agent: {await agent.RunAsync(input, session)}");
    var stored = session.TryGetInMemoryChatHistory(out var history) ? history!.Count : 0;
    Console.WriteLine($"  [stored history: {stored} messages | sent to model: {meter.LastAgentCallMessageCount}]\n");
}

Console.WriteLine("---- Recall across the compaction boundary ----\n");

const string recall = "Quick check: what's my name and customer id?";
Console.WriteLine($"User: {recall}");
Console.WriteLine($"Agent: {await agent.RunAsync(recall, session)}\n");

Console.WriteLine("---- What the model actually received on that last call ----\n");
foreach (var message in meter.LastAgentCallMessages)
{
    var text = message.Text.ReplaceLineEndings(" ");
    Console.WriteLine($"  {message.Role,-9} | {(text.Length > 90 ? text[..90] + "..." : text)}");
}
return;

static string GetOrderStatus(string orderId) =>
    // Deliberately bulky payload — exactly what ToolResultCompactionStrategy is for.
    $$"""
      {"orderId":"{{orderId}}","status":"in transit","carrier":"PostNord","eta":"2026-08-21",
       "history":[{"ts":"2026-08-14T08:12:00Z","event":"picked"},{"ts":"2026-08-14T16:40:00Z","event":"packed"},
                  {"ts":"2026-08-15T06:02:00Z","event":"handed to carrier"},{"ts":"2026-08-16T11:55:00Z","event":"at sorting hub DK-CPH"}],
       "items":[{"sku":"KB-201","qty":1,"name":"Mechanical keyboard"},{"sku":"MS-303","qty":2,"name":"Wireless mouse"}],
       "notes":"Signature required on delivery. Contact carrier for rerouting."}
      """;

static string GetShippingOptions(string country) =>
    $"Shipping to {country}: Standard (3-5 days, free), Express (1-2 days, 9 EUR), Same-day Copenhagen only (25 EUR).";

/// <summary>
/// Delegating IChatClient that records what the compacted agent invocation actually sends.
/// Summarization calls go straight to Settings.ChatClient, so only agent calls are metered.
/// </summary>
internal sealed class ModelInputMeter(IChatClient inner) : DelegatingChatClient(inner)
{
    public int LastAgentCallMessageCount { get; private set; }
    public IReadOnlyList<ChatMessage> LastAgentCallMessages { get; private set; } = [];

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastAgentCallMessages = messages.ToList();
        LastAgentCallMessageCount = LastAgentCallMessages.Count;
        return base.GetResponseAsync(LastAgentCallMessages, options, cancellationToken);
    }
}
