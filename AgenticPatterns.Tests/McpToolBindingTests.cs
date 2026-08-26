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

/// I1: the MCP sample used to pass `User: null` - the only caller-visible opt-out of a
/// SandboxOptions default in the tree - while README.md and PatternExplorer/patterns/MCP.md both
/// promised the "identical" locked-down boundary CodeAct gets, including `--user 65532:65532`.
/// It was non-root only because the image's own `USER mcp` line said so, and MCP.md tells readers
/// to point SandboxOptions.Image at any other sandboxed server. Verified by hand that the pinned
/// server runs fine under the explicit uid, so the boundary enforces it rather than trusting the
/// image - and this pins that so the next change to it is deliberate.
public class McpSandboxOptionsTests
{
    [Fact]
    public void TheMcpSandboxOptsOutOfNoDefaultAndRunsNonRoot()
    {
        var options = McpToolBinding.Sandbox();

        Assert.Equal("65532:65532", options.User);
        Assert.False(options.Network);
        Assert.Equal(McpToolBinding.ServerImage, options.Image);
        Assert.True(options.Interactive);   // the stdio transport needs -i
        Assert.NotNull(options.ContainerName);
        Assert.Null(options.Mounts);        // no host path is visible to the server
        Assert.Null(options.Environment);   // no host credential reaches it
    }

    [Fact]
    public void TheNonRootUserActuallyReachesDocker()
    {
        var args = Shared.Sandbox.SandboxRunner.BuildRunArguments(McpToolBinding.Sandbox(), []).ToList();
        Assert.Equal("65532:65532", args[args.IndexOf("--user") + 1]);
    }

    [Fact]
    public void EachRunGetsItsOwnContainerNameSoATimeoutKillsTheRightOne() =>
        Assert.NotEqual(McpToolBinding.Sandbox().ContainerName, McpToolBinding.Sandbox().ContainerName);
}
