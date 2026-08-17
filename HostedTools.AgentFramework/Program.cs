// Hosted (server-side) tools: the provider executes the tool, unlike the
// client-side AIFunctionFactory tools in the other samples. Hosted tools
// require the OpenAI Responses API (ResponsesClient), not chat completions.
// NOTE: this compiles against any config, but at runtime it only works on
// Azure OpenAI deployments that support the Responses API with hosted tools.

using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

#pragma warning disable OPENAI001 // Responses API is marked evaluation-only in the OpenAI SDK

var chatClient = new AzureOpenAIClient(
        new Uri(Settings.AzureOpenAi.Endpoint),
        new ApiKeyCredential(Settings.AzureOpenAi.ApiKey))
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
