using Shared.Sandbox;
using Xunit;

namespace AgenticPatterns.Tests;

public class SandboxArgumentTests
{
    [Fact]
    public void DefaultsDenyNetworkAndCapabilitiesAndWrites()
    {
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img"), ["echo", "hi"]).ToList();
        Assert.Equal("none", args[args.IndexOf("--network") + 1]);
        Assert.Contains("--read-only", args);
        Assert.Contains("--cap-drop", args);
        Assert.Contains("--pids-limit", args);
        Assert.DoesNotContain("--privileged", args);
    }

    [Fact]
    public void NoHostEnvironmentIsInherited()
    {
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img"), ["echo"]);
        // The container only sees variables passed explicitly; none were passed here.
        Assert.DoesNotContain("--env", args);
        Assert.DoesNotContain("-e", args);
    }

    // Controller ruling: BuildRunArguments emits "--mount type=bind,src=...,dst=...[,readonly]",
    // not "-v host:container:ro" — CodeActExecutionTests.OnlyThePerRunDirectoryIsMountedAndReadOnly
    // already pins this shape and cannot change.
    [Fact]
    public void MountsAreReadOnlyWhenRequested()
    {
        var options = new SandboxOptions("img", Mounts: [("/host/src", "/src", true)]);
        Assert.Contains(SandboxRunner.BuildRunArguments(options, ["echo"]),
            a => a == "type=bind,src=/host/src,dst=/src,readonly");
    }

    [Fact]
    public void EnablingTheNetworkIsExplicit()
    {
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img", Network: true), ["echo"]);
        Assert.DoesNotContain("none", args);
    }
}
