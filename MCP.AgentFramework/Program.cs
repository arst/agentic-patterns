using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Shared;

// 1) Connect to the official MCP demo server (stdio, no credentials needed)
await using var mcpClient = await McpClient.CreateAsync(
    new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "MCPServer",
        Command = "npx",
        Arguments = ["-y", "@modelcontextprotocol/server-everything"]
    }));

// 2) Discover tools
var tools = await mcpClient.ListToolsAsync().ConfigureAwait(false);
Console.WriteLine($"MCP tools: {string.Join(", ", tools.Select(t => t.Name))}");

// 3) McpClientTool derives from AIFunction, so MCP tools plug straight into the agent
var agent = new ChatClientAgent(Settings.ChatClient,
    "Use MCP tools when needed. Be concise and cite tool results in your reasoning.",
    tools: [.. tools.Cast<AITool>()]);

var prompt = "Use the 'add' tool to compute 1234 + 5678, then use the 'echo' tool to repeat the result.";

var response = await agent.RunAsync(prompt);
Console.WriteLine(response);
