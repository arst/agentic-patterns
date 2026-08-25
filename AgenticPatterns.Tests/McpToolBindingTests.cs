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
}
