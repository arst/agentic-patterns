using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;
using Shared;

#pragma warning disable SKEXP0001

await using var mcpClient = await McpClient.CreateAsync(
    new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "MCPServer",
        Command = "npx",
        // Official MCP demo server: runs over stdio, needs no credentials.
        Arguments = ["-y", "@modelcontextprotocol/server-everything"]
    }));

// 2) Discover tools
var tools = await mcpClient.ListToolsAsync().ConfigureAwait(false);

// 3) Register MCP tools as SK functions (agent can tool-call)
var kernel = Settings.Kernel;

// This pattern (MCP tools -> Kernel functions) is used in the official SK MCP sample.
kernel.Plugins.AddFromFunctions(
    "McpTools",
    tools.Select(aiFunction => aiFunction.AsKernelFunction()));

// 4) Enable auto function calling
var exec = new OpenAIPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
        options: new FunctionChoiceBehaviorOptions
        {
            RetainArgumentTypes = true
        })
};

// 5) Agent uses MCP tools as needed
var agent = new ChatCompletionAgent
{
    Name = "McpAgent",
    Instructions = "Use MCP tools when needed. Be concise and cite tool results in your reasoning.",
    Kernel = kernel,
    Arguments = new KernelArguments(exec)
};

var prompt = "Use the 'add' tool to compute 1234 + 5678, then use the 'echo' tool to repeat the result.";

await foreach (var response in agent.InvokeAsync(prompt))
    Console.WriteLine(response.Message.Content);