using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Shared;

var client = new Settings().ChatClient;

ChatClientAgent writer = new(
    client,
    "You are a creative copywriter. Generate catchy slogans. Be concise and impactful.",
    "CopyWriter",
    "A creative copywriter agent");

ChatClientAgent reviewer = new(
    client,
    "You are a marketing reviewer. Evaluate slogans for clarity and impact. Be very critical. Approve or suggest improvements.",
    "Reviewer",
    "A marketing review agent");

var workflow = AgentWorkflowBuilder
    .CreateGroupChatBuilderWith(agents =>
        new RoundRobinGroupChatManager(agents)
        {
            MaximumIterationCount = 5
        })
    .AddParticipants(writer, reviewer)
    .Build();

var messages = new List<ChatMessage>
{
    new(ChatRole.User, "Create a slogan for an eco-friendly electric vehicle.")
};

var run = await InProcessExecution.RunStreamingAsync(workflow, messages);
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
{
    if (evt.GetType() == typeof(WorkflowOutputEvent) && evt is WorkflowOutputEvent output)
    {
        var conversationHistory = output.As<List<ChatMessage>>();
        Console.WriteLine("\n=== Final Conversation ===");
        foreach (var message in conversationHistory)
        {
            Console.WriteLine($"{message.AuthorName}: {message.Text}");
        }
        break;
    }
}
