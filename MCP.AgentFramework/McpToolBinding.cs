namespace MCP.AgentFramework;

public static class McpToolBinding
{
    /// Discovery and authorization are separate steps: discovering a tool never grants it.
    /// Fails closed - a missing allowlisted tool means the server is not the one we pinned.
    public static IReadOnlyList<string> SelectAuthorized(IEnumerable<string> discovered,
        IReadOnlySet<string> allowed)
    {
        var found = discovered.Where(allowed.Contains).ToList();
        var missing = allowed.Except(found).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"The MCP server does not advertise the required tool(s): {string.Join(", ", missing)}.");
        return found;
    }
}
