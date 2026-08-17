using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Shared;

var chatClient = Settings.ChatClient;

var researcher = new ChatClientAgent(chatClient,
    "You're an expert market and product researcher. Provide concise, factual insights, opportunities, and risks.",
    "researcher");

var marketer = new ChatClientAgent(chatClient,
    "You're a creative marketing strategist. Craft compelling value propositions and target messaging aligned to the prompt.",
    "marketer");

var legal = new ChatClientAgent(chatClient,
    "You're a cautious legal/compliance reviewer. Highlight constraints, disclaimers, and policy concerns.",
    "legal");

// Built-in fan-out/fan-in: agents run concurrently, default aggregator collects all replies.
var workflow = AgentWorkflowBuilder.BuildConcurrent([researcher, marketer, legal]);

var run = await InProcessExecution.RunStreamingAsync(workflow, new ChatMessage(
    ChatRole.User,
    "Assess launching a new B2B analytics product in the EU. Provide recommendations."));
await run.TrySendMessageAsync(new TurnToken(emitEvents: false));

await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
    if (evt is WorkflowOutputEvent { Data: List<ChatMessage> messages })
        Console.WriteLine(string.Join(Environment.NewLine,
            messages.Select(m => $"##{m.AuthorName ?? m.Role.ToString()}: {m.Text.Trim()}")));
