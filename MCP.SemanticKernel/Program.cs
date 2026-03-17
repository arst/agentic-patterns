using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;
using Shared;

#pragma warning disable SKEXP0001

await using var mcpClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "MCPServer",
        Command = "npx",
        Arguments = ["-y", "--verbose", "@modelcontextprotocol/server-github"],
    }));

// 2) Discover tools
var tools = await mcpClient.ListToolsAsync().ConfigureAwait(false);

// 3) Register MCP tools as SK functions (agent can tool-call)
var kernel = new Settings().Kernel;

// This pattern (MCP tools -> Kernel functions) is used in the official SK MCP sample. :contentReference[oaicite:5]{index=5}
kernel.Plugins.AddFromFunctions(
    "McpTools",
    tools.Select(aiFunction => aiFunction.AsKernelFunction()));

// 4) Enable auto function calling
var exec = new OpenAIPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
        options: new()
        {
            RetainArgumentTypes = true
        })
};

// 5) Agent uses MCP tools as needed
var agent = new ChatCompletionAgent
{
    Name = "GitHubAgent",
    Instructions = "Use MCP tools when needed. Be concise and cite tool results in your reasoning.",
    Kernel = kernel,
    Arguments = new KernelArguments(exec)
};

var prompt = "Summarize the last four commits to the microsoft/semantic-kernel repository.";
var response = await agent.InvokeAsync(prompt).FirstAsync();

Console.WriteLine(response.Message.Content);