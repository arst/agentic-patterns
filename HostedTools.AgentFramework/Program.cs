// Hosted (server-side) tools: the provider executes the tool, unlike the
// client-side AIFunctionFactory tools in the other samples. Hosted tools
// require the OpenAI Responses API (ResponsesClient), not chat completions.
// NOTE: this compiles against any config, but at runtime it only works on
// Azure OpenAI deployments that support the Responses API with hosted tools.

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Shared;

#pragma warning disable OPENAI001 // Responses API is marked evaluation-only in the OpenAI SDK

// The plain OpenAI client against Azure's v1 endpoint ({endpoint}/openai/v1) — the GA surface
// for the Responses API. (Azure.AI.OpenAI 2.9.0-beta.1's GetResponsesClient() is binary-
// incompatible with the OpenAI 2.12 that MEAI 10.9 requires: MissingMethodException.)
var chatClient = new OpenAIClient(
        new ApiKeyCredential(Settings.AzureOpenAi.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(new Uri(Settings.AzureOpenAi.Endpoint), "openai/v1") })
    .GetResponsesClient()
    .AsIChatClient(Settings.AzureOpenAi.ChatModelDeployment);

var analyst = new ChatClientAgent(chatClient,
    "You are a data analyst. Write and run code to answer questions precisely.",
    "DataAnalyst",
    tools: [new HostedCodeInterpreterTool()]);

await RunAsync(analyst,
    "Simulate 10,000 rolls of two six-sided dice and report the distribution of sums as percentages.");

var researcher = new ChatClientAgent(chatClient,
    "You are a research assistant. Search the web for current information and cite your sources.",
    "WebResearcher",
    tools: [new HostedWebSearchTool()]);

await RunAsync(researcher,
    "What did Microsoft announce at the most recent .NET Conf?");

return;

static async Task RunAsync(AIAgent agent, string question)
{
    Console.WriteLine($"User: {question}");

    var response = await agent.RunAsync(question);

    // Hosted tool activity surfaces as content items, not FunctionCallContent.
    foreach (var content in response.Messages.SelectMany(m => m.Contents))
    {
        switch (content)
        {
            case CodeInterpreterToolCallContent code:
                Console.WriteLine("[code interpreter ran]");
                foreach (var input in code.Inputs?.OfType<TextContent>() ?? [])
                    Console.WriteLine(input.Text);
                break;
            case WebSearchToolCallContent search:
                Console.WriteLine($"[web search: {string.Join("; ", search.Queries ?? [])}]");
                break;
            case TextContent text:
                foreach (var citation in text.Annotations?.OfType<CitationAnnotation>() ?? [])
                    Console.WriteLine($"[citation] {citation.Title} - {citation.Url}");
                break;
        }
    }

    Console.WriteLine($"{agent.Name}: {response}\n");
}
