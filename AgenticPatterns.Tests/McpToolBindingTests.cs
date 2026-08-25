using MCP.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class McpToolBindingTests
{
    static readonly HashSet<string> Allowed = new(["add", "echo"], StringComparer.Ordinal);

    [Fact]
    public void OnlyAllowlistedToolsAreBound() =>
        Assert.Equal(["add", "echo"],
            McpToolBinding.SelectAuthorized(["add", "echo", "printEnv", "sampleLLM"], Allowed).Order());

    [Fact]
    public void AMissingAllowlistedToolFailsClosed() =>
        Assert.Throws<InvalidOperationException>(() => McpToolBinding.SelectAuthorized(["echo"], Allowed));

    [Fact]
    public void CaseInsensitiveAllowlistUsesTheAllowlistsOwnComparer()
    {
        var allowed = new HashSet<string>(["Add", "Echo"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["add", "echo"], McpToolBinding.SelectAuthorized(["add", "echo"], allowed).Order());
    }

    [Fact]
    public void DuplicateDiscoveredNamesAreNotDuplicatedInTheResult() =>
        Assert.Equal(["add", "echo"], McpToolBinding.SelectAuthorized(["add", "add", "echo"], Allowed).Order());
}
