using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.Handoff;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shared;

#pragma warning disable SKEXP0110

var setting = new Settings();
var kernel = Kernel.CreateBuilder()
    .AddAzureOpenAIChatCompletion(
        setting.AzureOpenAi.ChatModelDeployment,
        setting.AzureOpenAi.Endpoint,
        setting.AzureOpenAi.ApiKey)
    .Build();

ChatCompletionAgent triageAgent = new()
{
    Name = "TriageAgent",
    Description = "Routes requests to the right specialist.",
    Instructions = """
                   You are a triage agent. Decide which specialist should handle the user's request:
                   - BillingAgent: invoices, refunds, payments
                   - TechAgent: bugs, errors, troubleshooting
                   - AccountAgent: login, profile, access
                   If unclear, ask a short clarifying question.
                   """,
    Kernel = kernel
};

ChatCompletionAgent billingAgent = new()
{
    Name = "BillingAgent",
    Description = "Handles billing issues.",
    Instructions = "You are a billing specialist. Solve billing problems concisely.",
    Kernel = kernel
};

ChatCompletionAgent techAgent = new()
{
    Name = "TechAgent",
    Description = "Handles technical issues.",
    Instructions = "You are a technical support specialist. Troubleshoot step-by-step.",
    Kernel = kernel
};

ChatCompletionAgent accountAgent = new()
{
    Name = "AccountAgent",
    Description = "Handles account issues.",
    Instructions = "You are an account specialist. Help with login/access/profile.",
    Kernel = kernel
};

var handoffs = OrchestrationHandoffs
    .StartWith(triageAgent)
    .Add(triageAgent, billingAgent, techAgent, accountAgent)
    .Add(billingAgent, triageAgent, "Transfer here if it's not billing related")
    .Add(techAgent, triageAgent, "Transfer here if it's not technical support related")
    .Add(accountAgent, triageAgent, "Transfer here if it's not account related");


ChatHistory history = [];

ValueTask responseCallback(ChatMessageContent msg)
{
    history.Add(msg);
    Console.WriteLine(
        $"{msg.AuthorName ?? msg.Role.ToString()}: {msg.Content ?? (msg as OpenAIChatMessageContent)!.ToolCalls[0].FunctionName}");
    return ValueTask.CompletedTask;
}

Queue<string> userInputs = new();
userInputs.Enqueue("I was charged twice on my last invoice.");

ValueTask<ChatMessageContent> interactiveCallback()
{
    var input = userInputs.Dequeue();
    Console.WriteLine($"\nUser: {input}");
    return ValueTask.FromResult(new ChatMessageContent(AuthorRole.User, input));
}


HandoffOrchestration orchestration = new(
    handoffs,
    triageAgent,
    billingAgent,
    techAgent,
    accountAgent)
{
    InteractiveCallback = interactiveCallback,
    ResponseCallback = responseCallback
};

InProcessRuntime runtime = new();
await runtime.StartAsync();

var task = "A customer is on the line.";
var result = await orchestration.InvokeAsync(task, runtime);

// Wait for the orchestration to complete
var finalOutput = await result.GetValueAsync(TimeSpan.FromMinutes(2));
Console.WriteLine($"\n=== Final Output ===\n{finalOutput}");
await runtime.RunUntilIdleAsync();