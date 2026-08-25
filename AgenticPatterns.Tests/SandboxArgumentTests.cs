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

    // ---- M3: --memory 0 / --cpus 0 read to docker as UNLIMITED, the same fail-open shape ----

    [Theory]
    [InlineData("0")]
    [InlineData("0m")]
    [InlineData("0.0")]
    [InlineData("")]
    [InlineData(null)]
    public void EffectiveMemoryClampsAnythingDockerWouldReadAsUnlimited(string? configured) =>
        Assert.Equal("512m", SandboxRunner.EffectiveMemory(configured));

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("")]
    [InlineData(null)]
    public void EffectiveCpusClampsAnythingDockerWouldReadAsUnlimited(string? configured) =>
        Assert.Equal("1", SandboxRunner.EffectiveCpus(configured));

    [Fact]
    public void EffectiveMemoryAndCpusNeverOverrideAnExplicitCallerValue()
    {
        Assert.Equal("1g", SandboxRunner.EffectiveMemory("1g"));
        Assert.Equal("2", SandboxRunner.EffectiveCpus("2"));
    }

    [Fact]
    public void ZeroMemoryAndCpusReachDockerAsTheSafeDefaults()
    {
        var args = SandboxRunner.BuildRunArguments(
            new SandboxOptions("img", Memory: "0", Cpus: "0"), ["echo"]).ToList();
        Assert.Equal("512m", args[args.IndexOf("--memory") + 1]);
        Assert.Equal("1", args[args.IndexOf("--cpus") + 1]);
    }

    // ---- M4: `--mount` is a comma-separated option list with no escaping mechanism ----

    [Fact]
    public void AMountPathContainingACommaIsRejectedRatherThanInjectingMountOptions()
    {
        var options = new SandboxOptions("img", Mounts: [("/host/a,readwrite,/etc", "/src", true)]);
        Assert.Throws<ArgumentException>(() => SandboxRunner.BuildRunArguments(options, ["echo"]));
    }

    // ---- I1: the non-root default the README and MCP.md both promise ----

    [Fact]
    public void TheDefaultUserIsNonRootAndIsActuallyPassedToDocker()
    {
        var args = SandboxRunner.BuildRunArguments(new SandboxOptions("img"), ["echo"]).ToList();
        Assert.Equal("65532:65532", args[args.IndexOf("--user") + 1]);
    }
}

// The clamp VALUE is asserted purely above (EffectiveTimeout*). What a pure test cannot prove is
// that RunAsync actually wires it into CancelAfter — that needs a real run whose command outlives
// the bound. The previous version of this test ran `dotnet --version` and asserted TimedOut is
// false, which stayed green with `timeoutCts.CancelAfter(...)` deleted, and green on CI where the
// image it named (built only by ContainerCodeRunner.EnsureImageAsync) is absent and `docker run`
// exits 125 having launched nothing. This one uses the SDK image StigmergicBuildGateSandboxTests
// already depends on — pulled if absent — and asserts the timeout FIRES, so deleting the
// CancelAfter line makes it fail rather than pass. Docker-gated like CodeActSandboxSmokeTests.
public class SandboxTimeoutTests
{
    private static readonly bool DockerAvailable = SandboxRunner.IsAvailable("docker");

    [Fact]
    public async Task TheConfiguredTimeoutIsWiredIntoTheRunAndKillsTheContainer()
    {
        if (!DockerAvailable) return;

        var options = new SandboxOptions("mcr.microsoft.com/dotnet/sdk:10.0",
            Timeout: TimeSpan.FromSeconds(3),
            ContainerName: $"sandbox-timeout-test-{Guid.NewGuid():N}");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await SandboxRunner.RunAsync(options, ["sleep", "120"], stdin: null, CancellationToken.None);
        started.Stop();

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        // Bounded well below `sleep 120`: proves the run was cut short, not merely reported as such.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(90), $"took {started.Elapsed}");
    }
}
