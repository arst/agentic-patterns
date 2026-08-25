using MCP.SemanticKernel;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;
using Shared;
using Shared.Sandbox;

#pragma warning disable SKEXP0001

const string image = "agentic-patterns/mcp-server-everything:2025.8.18";
var allowed = new HashSet<string>(["add", "echo"], StringComparer.Ordinal);

// Fail closed: no sandbox, no MCP server. Same double opt-in as CodeAct for the unsafe path.
if (!SandboxRunner.IsAvailable("docker"))
{
    Console.Error.WriteLine(
        "No container runtime available. This sample runs a third-party MCP server, which is " +
        "untrusted code; it will not be started on the host. Install Docker, or set " +
        "AGENTIC_PATTERNS_ALLOW_UNSAFE_HOST_EXECUTION=true and " +
        "AGENTIC_PATTERNS_ACKNOWLEDGE_UNSAFE_CODE_EXECUTION=I_UNDERSTAND_THIS_RUNS_UNTRUSTED_CODE_ON_MY_HOST.");
    return 1;
}

// The server speaks stdio, so the container IS the transport: no network, no host environment,
// no credentials, read-only filesystem, dropped capabilities, bounded pids and memory.
var sandbox = new SandboxOptions(image, Network: false, Memory: "256m", PidsLimit: 64, Interactive: true,
    User: null);
await using var mcpClient = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "MCPServer",
    Command = "docker",
    Arguments = [.. SandboxRunner.BuildRunArguments(sandbox, [])],
}));

var discovered = await mcpClient.ListToolsAsync().ConfigureAwait(false);
Console.WriteLine($"Discovered: {string.Join(", ", discovered.Select(t => t.Name))}");
var authorized = McpToolBinding.SelectAuthorized(discovered.Select(t => t.Name), allowed).ToHashSet();
Console.WriteLine($"Bound to the agent: {string.Join(", ", authorized)}");

// Register MCP tools as SK functions (agent can tool-call) - allowlisted tools only.
var kernel = Settings.Kernel;
kernel.Plugins.AddFromFunctions(
    "McpTools",
    discovered.Where(t => authorized.Contains(t.Name)).Select(aiFunction => aiFunction.AsKernelFunction()));

// Enable auto function calling
var exec = new OpenAIPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
        options: new FunctionChoiceBehaviorOptions
        {
            RetainArgumentTypes = true
        })
};

// Agent uses MCP tools as needed
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
return 0;
