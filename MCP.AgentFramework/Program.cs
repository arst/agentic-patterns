using MCP.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Shared;
using Shared.Sandbox;

const string image = "agentic-patterns/mcp-server-everything:2025.8.18";
var allowed = new HashSet<string>(["add", "echo"], StringComparer.Ordinal);

// Fail closed: no sandbox, no MCP server. Unlike CodeAct, this sample has no host-execution
// fallback at all - there is nothing to opt into, so the message must not imply otherwise.
if (!SandboxRunner.IsAvailable("docker"))
{
    Console.Error.WriteLine(
        "No container runtime available. This sample runs a third-party MCP server, which is " +
        "untrusted code; it will not be started on the host. Install Docker or Podman to run it.");
    return 1;
}

// The server speaks stdio, so the container IS the transport: no network, no host environment,
// no credentials, read-only filesystem, dropped capabilities, bounded pids and memory. Named
// explicitly (not just left to SandboxRunner.RunAsync's own naming, which this stdio path
// doesn't go through) so the container can be torn down by name if the process is killed -
// SIGKILLing the `docker run` CLI does not stop the daemon-side container.
// ponytail: no automatic kill-by-name wired up on this path (McpClient owns the process, not
// RunAsync) - add it if this sample stops being a short-lived demo.
var sandbox = new SandboxOptions(image, Network: false, Memory: "256m", PidsLimit: 64, Interactive: true,
    User: null, ContainerName: $"mcp-sandbox-{Guid.NewGuid():N}");
await using var mcpClient = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "MCPServer",
    Command = "docker",
    Arguments = [.. SandboxRunner.BuildRunArguments(sandbox, [])],
}));

var discovered = await mcpClient.ListToolsAsync();
Console.WriteLine($"Discovered: {string.Join(", ", discovered.Select(t => t.Name))}");
var authorized = McpToolBinding.SelectAuthorized(discovered.Select(t => t.Name), allowed).ToHashSet();
Console.WriteLine($"Bound to the agent: {string.Join(", ", authorized)}");

var agent = new ChatClientAgent(Settings.ChatClient,
    "Use MCP tools when needed. Be concise and cite tool results in your reasoning.",
    tools: [.. discovered.Where(t => authorized.Contains(t.Name)).Cast<AITool>()]);

var prompt = "Use the 'add' tool to compute 1234 + 5678, then use the 'echo' tool to repeat the result.";

var response = await agent.RunAsync(prompt);
Console.WriteLine(response);
return 0;
