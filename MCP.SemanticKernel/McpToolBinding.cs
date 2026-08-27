namespace MCP.SemanticKernel;

using Shared.Sandbox;

public static class McpToolBinding
{
    /// The pinned server image. Built by hand before the run - see MCP.AgentFramework/Sandbox/Dockerfile.
    public const string ServerImage = "agentic-patterns/mcp-server-everything:2025.8.18";

    /// <summary>
    /// The sandbox the pinned server runs in. The server speaks stdio, so the container IS the
    /// transport: no network, no host environment, no credentials, read-only filesystem, dropped
    /// capabilities, non-root, bounded pids and memory. Every one of those is a
    /// <see cref="SandboxOptions"/> DEFAULT - this sample opts out of none of them, which is what
    /// lets README.md and PatternExplorer/patterns/MCP.md say "the identical locked-down-container
    /// boundary CodeAct uses" without qualification. Lives here rather than inline in each
    /// Program.cs so both flavors share one definition and a test can pin it.
    ///
    /// Named explicitly (not left to SandboxRunner.RunAsync's own naming, which this stdio path
    /// doesn't go through) so the container can be torn down BY NAME - SIGKILLing the `docker run`
    /// CLI that McpClient owns does not stop the daemon-side container. Program.cs removes it in a
    /// finally block via SandboxRunner.RemoveContainerAsync: lifecycle cleanup is part of the
    /// sandbox guarantee, so it must not depend on the transport disposing cleanly.
    /// </summary>
    public static SandboxOptions Sandbox() => new(
        ServerImage, Network: false, Memory: "256m", PidsLimit: 64, Interactive: true,
        ContainerName: $"mcp-sandbox-{Guid.NewGuid():N}");

    /// Discovery and authorization are separate steps: discovering a tool never grants it.
    /// Fails closed - a missing allowlisted tool means the server is not the one we pinned.
    /// Takes a HashSet (not IReadOnlySet) so its own Comparer governs both the match and the
    /// missing-check - one comparer for the whole function, not the set's for one direction
    /// and the default for the other.
    public static IReadOnlyList<string> SelectAuthorized(IEnumerable<string> discovered,
        HashSet<string> allowed)
    {
        var found = discovered.Where(allowed.Contains).Distinct(allowed.Comparer).ToList();
        var missing = allowed.Except(found, allowed.Comparer).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"The MCP server does not advertise the required tool(s): {string.Join(", ", missing)}.");
        return found;
    }
}
