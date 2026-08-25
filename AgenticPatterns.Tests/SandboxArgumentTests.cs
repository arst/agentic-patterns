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
        // M2: assert intent (no --network flag at all), not just the absence of the
        // string "none" — a weaker assertion would pass even if the flag leaked through
        // with some other value.
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img", Network: true), ["echo"]);
        Assert.DoesNotContain("--network", args);
    }

    // ---- I4: the four fields ruling 2 added, pinned individually ----

    [Fact]
    public void NullUserOmitsTheFlagAndItsValue()
    {
        // Task 2.2 depends on this exact behaviour: User: null defers to the image's own USER.
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img", User: null), ["echo"]);
        Assert.DoesNotContain("--user", args);
        Assert.DoesNotContain("65532:65532", args);
    }

    [Fact]
    public void InteractiveFlagIsPlacedBeforeTheImage()
    {
        // MCP's stdio transport depends on -i being a docker FLAG, i.e. before the image
        // argument, not appended after it (where docker would pass it to the command instead).
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img", Interactive: true), ["echo"]).ToList();
        Assert.True(args.IndexOf("-i") < args.IndexOf("img"));
    }

    [Fact]
    public void NullContainerNameOmitsTheFlag()
    {
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img", ContainerName: null), ["echo"]);
        Assert.DoesNotContain("--name", args);
    }

    [Fact]
    public void NullTmpfsOmitsTheFlag()
    {
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img", Tmpfs: null), ["echo"]);
        Assert.DoesNotContain("--tmpfs", args);
    }

    [Fact]
    public void NonPositivePidsLimitFallsBackToTheSafeDefault()
    {
        // M3: 0 (or negative) reads to docker as "unlimited" — the same fail-open-on-a-bound
        // shape as an unset Timeout must not be reachable through PidsLimit either.
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img", PidsLimit: 0), ["echo"]).ToList();
        Assert.Equal("128", args[args.IndexOf("--pids-limit") + 1]);
    }
}

// I1 has no coverage from BuildRunArguments alone — the clamp lives in RunAsync's
// CancelAfter call, so proving it needs a real run. Gated on Docker like
// CodeActSandboxSmokeTests: passes vacuously without it rather than failing the suite.
public class SandboxTimeoutTests
{
    private static readonly bool DockerAvailable = SandboxRunner.IsAvailable("docker");

    [Fact]
    public async Task ZeroTimeoutFallsBackToTheSafeDefaultInsteadOfCancellingImmediately()
    {
        if (!DockerAvailable) return;

        // Timeout: default (TimeSpan.Zero) would cancel a CancellationTokenSource
        // instantly if taken literally; RunAsync must clamp it to a safe positive bound
        // instead, so an ordinary, fast command still completes with TimedOut: false.
        var options = new SandboxOptions("agentic-patterns-codeact-sandbox",
            ContainerName: $"sandbox-timeout-test-{Guid.NewGuid():N}");
        var result = await SandboxRunner.RunAsync(options, ["dotnet", "--version"], stdin: null, CancellationToken.None);

        Assert.False(result.TimedOut);
    }
}
