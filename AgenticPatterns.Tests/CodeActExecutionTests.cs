using CodeAct.AgentFramework.Execution;
using Shared.Sandbox;
using Xunit;

#pragma warning disable CS0618 // testing the deliberately-[Obsolete] unsafe runner is the point

namespace AgenticPatterns.Tests;

// These tests verify the SECURITY CONTROL FLOW of the CodeAct executor without a model
// or a container runtime: runner selection fails closed, the unsafe path needs a double
// opt-in, and the container arguments grant nothing beyond what `dotnet run` needs.
public class CodeActExecutionTests
{
    private static readonly CodeExecutionOptions Options = new();

    // xunit runs tests in one class sequentially, so mutating the process environment
    // here cannot race another test in this class.
    private static T WithEnvironmentVariable<T>(string name, string? value, Func<T> body)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }

    private static T WithAcknowledgement<T>(string? value, Func<T> body) =>
        WithEnvironmentVariable(CodeRunnerFactory.UnsafeAcknowledgementVariable, value, body);

    // ---- runner selection ----

    [Fact]
    public void ContainerRunnerIsSelectedByDefault() =>
        Assert.IsType<ContainerCodeRunner>(
            CodeRunnerFactory.Create(Options, containerRuntimeAvailable: true));

    [Fact]
    public void MissingContainerRuntimeFailsClosed() =>
        WithAcknowledgement<object?>(null, () =>
            Assert.Throws<InvalidOperationException>(() =>
                CodeRunnerFactory.Create(Options, containerRuntimeAvailable: false)));

    [Fact]
    public void UnsafeFlagAloneIsInsufficient() =>
        WithAcknowledgement<object?>(null, () =>
            Assert.Throws<InvalidOperationException>(() =>
                CodeRunnerFactory.Create(Options with { AllowUnsafeHostExecution = true },
                    containerRuntimeAvailable: false)));

    [Fact]
    public void AcknowledgementVariableAloneIsInsufficient() =>
        WithAcknowledgement<object?>(CodeRunnerFactory.UnsafeAcknowledgementValue, () =>
            Assert.Throws<InvalidOperationException>(() =>
                CodeRunnerFactory.Create(Options, containerRuntimeAvailable: false)));

    [Fact]
    public void WrongAcknowledgementValueIsInsufficient() =>
        WithAcknowledgement<object?>("yes", () =>
            Assert.Throws<InvalidOperationException>(() =>
                CodeRunnerFactory.Create(Options with { AllowUnsafeHostExecution = true },
                    containerRuntimeAvailable: false)));

    [Fact]
    public void DoubleOptInSelectsUnsafeHostRunner() =>
        WithAcknowledgement(CodeRunnerFactory.UnsafeAcknowledgementValue, () =>
            Assert.IsType<UnsafeHostCodeRunner>(
                CodeRunnerFactory.Create(Options with { AllowUnsafeHostExecution = true },
                    containerRuntimeAvailable: false)));

    [Fact]
    public void ContainerAvailabilityBeatsTheUnsafeOptIn() =>
        WithAcknowledgement(CodeRunnerFactory.UnsafeAcknowledgementValue, () =>
            Assert.IsType<ContainerCodeRunner>(
                CodeRunnerFactory.Create(Options with { AllowUnsafeHostExecution = true },
                    containerRuntimeAvailable: true)));

    [Fact]
    public void UnsafeExecutionCanBeRequestedByFlagOrEnvironmentVariable()
    {
        Assert.True(CodeRunnerFactory.IsUnsafeHostExecutionRequested([CodeRunnerFactory.UnsafeEnableFlag]));
        Assert.True(WithEnvironmentVariable(CodeRunnerFactory.UnsafeEnableVariable,
            CodeRunnerFactory.UnsafeEnableValue,
            () => CodeRunnerFactory.IsUnsafeHostExecutionRequested([])));
    }

    // ---- container arguments: least privilege, pinned flag by flag ----

    private static List<string> Args(string runDir = "/tmp/codeact/run1") =>
        [.. ContainerCodeRunner.BuildRunArguments("codeact-run1", runDir, Options)];

    private static string ValueOf(List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, $"missing {flag}");
        return args[index + 1];
    }

    [Fact]
    public void ContainerHasNoNetwork() => Assert.Equal("none", ValueOf(Args(), "--network"));

    [Fact]
    public void ContainerFilesystemIsReadOnly() => Assert.Contains("--read-only", Args());

    [Fact]
    public void AllCapabilitiesAreDropped() => Assert.Equal("ALL", ValueOf(Args(), "--cap-drop"));

    [Fact]
    public void PrivilegeEscalationIsBlocked() =>
        Assert.Equal("no-new-privileges=true", ValueOf(Args(), "--security-opt"));

    [Fact]
    public void ContainerRunsAsNonRoot() => Assert.Equal("65532:65532", ValueOf(Args(), "--user"));

    [Fact]
    public void ProcessMemoryAndCpuAreLimited()
    {
        var args = Args();
        Assert.Equal("128", ValueOf(args, "--pids-limit"));
        Assert.Equal("1g", ValueOf(args, "--memory"));
        Assert.Equal("1", ValueOf(args, "--cpus"));
    }

    [Fact]
    public void OnlyThePerRunDirectoryIsMountedAndReadOnly()
    {
        var args = Args("/tmp/codeact/run1");
        var mounts = args.Where((a, i) => i > 0 && args[i - 1] == "--mount").ToList();
        var mount = Assert.Single(mounts);
        Assert.Contains("src=/tmp/codeact/run1", mount);
        Assert.Contains("readonly", mount);
    }

    [Fact]
    public void NoHostEnvironmentVariableIsForwarded()
    {
        var args = Args();
        var envs = args.Where((a, i) => i > 0 && args[i - 1] == "--env")
                       .Select(e => e.Split('=')[0]);
        // The complete allowlist the SDK needs to run offline as an unknown non-root user.
        Assert.Equal(["HOME", "DOTNET_CLI_HOME", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT"], envs);
    }

    [Fact]
    public void TheOnlyWritablePathIsABoundedTmpfs()
    {
        var tmpfs = ValueOf(Args(), "--tmpfs");
        Assert.StartsWith("/tmp:", tmpfs);
        Assert.Contains("nosuid", tmpfs);
        Assert.Contains("size=", tmpfs);
    }

    [Fact]
    public void HostCannotBeAskedToRunAnythingButTheScript()
    {
        // Command tail is fixed: image, then `dotnet run /workspace/script.cs`.
        Assert.Equal([Options.ContainerImage, "dotnet", "run", "/workspace/script.cs"], Args()[^4..]);
    }

    // ---- output and cancellation lifecycle ----

    [Fact]
    public async Task OutputRetentionIsBoundedButThePipeIsFullyDrained()
    {
        var reader = new StringReader(new string('x', 50_000));
        var result = await BoundedReader.ReadBoundedAsync(reader, 1_000, CancellationToken.None);

        Assert.StartsWith(new string('x', 1_000), result);
        Assert.EndsWith("[output truncated]", result);
        Assert.Equal(-1, reader.Read()); // drained to end-of-stream, not abandoned
    }

    [Fact]
    public async Task OutputUnderTheLimitIsReturnedVerbatim()
    {
        var result = await BoundedReader.ReadBoundedAsync(new StringReader("hello"), 1_000, CancellationToken.None);
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoATimeoutResult()
    {
        var runner = new UnsafeHostCodeRunner(Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("Console.WriteLine();", new CancellationToken(canceled: true)));
    }
}
