using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Routing.AgentFramework.Workflow;
using Routing.AgentFramework.Workflow.Executors;
using Shared;

var setting = new Settings();
var chatClient = new AzureOpenAIClient(
        new Uri(setting.AzureOpenAi.Endpoint),
        new ApiKeyCredential(setting.AzureOpenAi.ApiKey))
    .GetChatClient(setting.AzureOpenAi.ChatModelDeployment)
    .AsIChatClient();


var intake = new IntakeExecutor();
var router = new RouterExecutor(new ChatClientAgent(chatClient));
var billing = new BillingExecutor(new ChatClientAgent(chatClient));
var technical = new TechnicalExecutor(new ChatClientAgent(chatClient));
var account = new AccountExecutor(new ChatClientAgent(chatClient));
var general = new GeneralExecutor(new ChatClientAgent(chatClient));
var responseComposer = new ResponseComposer();

var workflow =
    new WorkflowBuilder(intake)
        .AddEdge(intake, router)
        .AddEdge<RouteDecision>(router, billing,
            data => data?.Route == Route.Billing)
        .AddEdge<RouteDecision>(router, technical,
            data => data?.Route == Route.Technical)
        .AddEdge<RouteDecision>(router, account,
            data => data?.Route == Route.Account)
        .AddEdge<RouteDecision>(router, general,
            data => data?.Route == Route.General)
        .AddEdge(billing, responseComposer)
        .AddEdge(technical, responseComposer)
        .AddEdge(account, responseComposer)
        .AddEdge(general, responseComposer)
        .WithOutputFrom(responseComposer)
        .Build();


var run = await InProcessExecution.RunStreamingAsync(
    workflow,
    "I was charged twice last month.");

await foreach (var evt in run.WatchStreamAsync())
    switch (evt)
    {
        case ExecutorInvokedEvent invoke:
            Console.WriteLine($"Starting {invoke.ExecutorId}");
            break;

        case ExecutorCompletedEvent complete:
            Console.WriteLine($"Completed {complete.ExecutorId}: {complete.Data}");
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine($"Workflow output: {output.Data}");
            return;

        case WorkflowErrorEvent error:
            Console.WriteLine($"Workflow error: {error.Exception}");
            return;
    }