using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using PromptChaining.AgentFramework;
using Shared;

var chatClient = new AzureOpenAIClient(
        new Uri(Settings.AzureOpenAi.Endpoint),
        new ApiKeyCredential(Settings.AzureOpenAi.ApiKey))
    .GetChatClient(Settings.AzureOpenAi.ChatModelDeployment)
    .AsIChatClient();

var summarizerAgent = new ChatClientAgent(chatClient, name: "SummarizerAgent",
    instructions: "You are a helpful summarizer.");

var emailAgent = new ChatClientAgent(chatClient, name: "EmailGeneratorAgent",
    instructions: "You write concise internal emails to leadership (max 150 words).");


var extractorExec = new ExtractorExecutor(chatClient);
var summarizerExec = new SummarizerExecutor(summarizerAgent);
var emailExec = new EmailExecutor(emailAgent);

var workflow = new WorkflowBuilder(extractorExec)
    .AddEdge(extractorExec, summarizerExec)
    .AddEdge(summarizerExec, emailExec)
    .WithOutputFrom(emailExec)
    .Build();

var input = """
            Contoso is considering acquiring Fabrikam. Alice (CFO) said the top priorities are:
            reducing cloud spend and accelerating time-to-market. The decision is expected in Q2.
            """;

await using var run = await InProcessExecution.RunStreamingAsync(workflow, input);
await foreach (var evt in run.WatchStreamAsync())
    if (evt is WorkflowOutputEvent outputEvt)
        Console.WriteLine(outputEvt.Data);