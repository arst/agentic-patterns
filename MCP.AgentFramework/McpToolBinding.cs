namespace MCP.AgentFramework;

public static class McpToolBinding
{
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
