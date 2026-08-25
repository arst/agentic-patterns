using MCP.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Shared;
using Shared.Sandbox;

var allowed = new HashSet<string>(["add", "echo"], StringComparer.Ordinal);

// Fail closed: no sandbox, no MCP server. Unlike CodeAct, this sample has no host-execution
// fallback at all - there is nothing to opt into, so the message must not imply otherwise.
// It names only Docker because that is the only runtime this sample can actually use: unlike
// CodeAct, which takes the runtime from CodeExecutionOptions.ContainerRuntime, the command
// below is hardcoded. Advertising Podman here would leave a podman-only user installing
// exactly what the message asked for and getting the identical message forever.
if (!SandboxRunner.IsAvailable("docker"))
{
    Console.Error.WriteLine(
        "Docker is not available. This sample runs a third-party MCP server, which is " +
        "untrusted code; it will not be started on the host. Install Docker to run it.");
    return 1;
}

// Every isolation flag - including the non-root --user default the docs promise - comes from
// McpToolBinding.Sandbox(); this sample opts out of nothing. See its doc comment.
var sandbox = McpToolBinding.Sandbox();
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
