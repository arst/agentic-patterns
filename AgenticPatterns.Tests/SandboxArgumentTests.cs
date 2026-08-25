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

    // ---- fix round 2: assert the clamps as pure values, not by waiting for them ----
    // A test that runs a fast command and asserts TimedOut: false cannot fail if the clamp
    // is reverted — a fast command finishes long before either an unbounded wait or a
    // 3-minute bound expires. Assert the decision directly instead.

    [Fact]
    public void EffectiveTimeoutClampsNonPositiveValuesToThreeMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), SandboxRunner.EffectiveTimeout(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(3), SandboxRunner.EffectiveTimeout(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void EffectiveTimeoutNeverOverridesAnExplicitCallerValue() =>
        Assert.Equal(TimeSpan.FromSeconds(30), SandboxRunner.EffectiveTimeout(TimeSpan.FromSeconds(30)));

    [Fact]
    public void EffectivePidsLimitClampsNonPositiveValuesTo128()
    {
        Assert.Equal(128, SandboxRunner.EffectivePidsLimit(0));
        Assert.Equal(128, SandboxRunner.EffectivePidsLimit(-1));
    }

    [Fact]
    public void EffectivePidsLimitNeverOverridesAnExplicitCallerValue() =>
        Assert.Equal(64, SandboxRunner.EffectivePidsLimit(64));
}

// The zero-Timeout clamp value itself is asserted directly above (EffectiveTimeoutClamps...).
// What that pure test cannot prove is that RunAsync actually WIRES the clamp into
// CancelAfter instead of, say, ignoring Timeout entirely — that needs a real run. Gated on
// Docker like CodeActSandboxSmokeTests: passes vacuously without it rather than failing the suite.
public class SandboxTimeoutTests
{
    private static readonly bool DockerAvailable = SandboxRunner.IsAvailable("docker");

    [Fact]
    public async Task ZeroTimeoutDoesNotCancelTheRunImmediately()
    {
        if (!DockerAvailable) return;

        var options = new SandboxOptions("agentic-patterns-codeact-sandbox",
            ContainerName: $"sandbox-timeout-test-{Guid.NewGuid():N}");
        var result = await SandboxRunner.RunAsync(options, ["dotnet", "--version"], stdin: null, CancellationToken.None);

        Assert.False(result.TimedOut);
    }
}
