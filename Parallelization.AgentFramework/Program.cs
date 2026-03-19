using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Parallelization.AgentFramework;
using Shared;

var chatClient = Settings.ChatClient;

var researcher = new ChatExecutor("researcher", new ChatClientAgent(chatClient,
    "You're an expert market and product researcher. Provide concise, factual insights, opportunities, and risks.",
    "researcher"));

var marketer = new ChatExecutor("marketer", new ChatClientAgent(chatClient,
    "You're a creative marketing strategist. Craft compelling value propositions and target messaging aligned to the prompt.",
    "marketer"));

var legal = new ChatExecutor("legal", new ChatClientAgent(chatClient,
    "You're a cautious legal/compliance reviewer. Highlight constraints, disclaimers, and policy concerns.",
    "legal"));

var input = new ChatForwardingExecutor("input", new ChatForwardingExecutorOptions());
var output = new AggregatingExecutor<ChatMessage, List<ChatMessage>>("aggregator", (agg, item) =>
{
    var list = agg is null
        ? new List<ChatMessage>()
        : new List<ChatMessage>(agg);
    list.Add(item);
    return list;
});


var workflow = new WorkflowBuilder(input)
    .AddFanOutEdge(input, [researcher, marketer, legal])
    .AddFanInBarrierEdge([researcher, marketer, legal], output)
    .WithOutputFrom(output)
    .Build();

var run = await InProcessExecution.RunStreamingAsync(workflow, new ChatMessage(
    ChatRole.User,
    "Assess launching a new B2B analytics product in the EU. Provide recommendations."));

await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
    if (evt is WorkflowOutputEvent outputEvent)
    {
        var data = outputEvent.Data as List<ChatMessage>;
        if (data is not null && data.Count == 3)
            Console.WriteLine(string.Join(Environment.NewLine, data.Select(m => $"##{m.Role}: {m.Text.Trim()}")));
    }