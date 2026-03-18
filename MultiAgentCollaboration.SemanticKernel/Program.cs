using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Shared;

#pragma warning disable SKEXP0110

var kernel = new Settings().Kernel;

var writer = new ChatCompletionAgent
{
    Name = "CopyWriter",
    Description = "A copy writer",
    Instructions = "Write one strong slogan. Be brief. No chatter.",
    Kernel = kernel
};

var reviewer = new ChatCompletionAgent
{
    Name = "Reviewer",
    Description = "An editor",
    Instructions = "Critique the slogan. If acceptable, say 'Approved'. If not, give guidance without rewriting.",
    Kernel = kernel
};

ChatHistory history = [];

ValueTask responseCallback(ChatMessageContent msg)
{
    history.Add(msg);
    Console.WriteLine($"{msg.AuthorName}: {msg.Content}");
    return ValueTask.CompletedTask;
}

var orchestration = new GroupChatOrchestration(
    new RoundRobinGroupChatManager { MaximumInvocationCount = 5 },
    writer,
    reviewer)
{
    ResponseCallback = responseCallback
};

var runtime = new InProcessRuntime();
await runtime.StartAsync();

var result = await orchestration.InvokeAsync(
    "Create a slogan for a new electric SUV that is affordable and fun to drive.",
    runtime);

var final = await result.GetValueAsync(TimeSpan.FromSeconds(60));
Console.WriteLine($"\n=== FINAL ===\n{final}");

await runtime.RunUntilIdleAsync();