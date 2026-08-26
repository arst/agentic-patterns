using Shared.Sandbox;
using StigmergicCoordination.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

// Security control flow of the stigmergic build gate without a model or a container runtime:
// the size cap fires before anything is compiled, error lines are parsed correctly, the
// sandboxed options grant nothing beyond what an offline `dotnet build` needs, and the host
// fallback needs the same double opt-in as CodeAct.
public class StigmergicBuildGateTests
{
    private static T WithEnvironmentVariable<T>(string name, string? value, Func<T> body)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }

    // ---- size cap ----

    [Fact]
    public void OversizedFileIsRejectedBeforeAnythingCompiles()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(workspace, "Big.cs"), new string('x', (int)BuildGate.MaxSourceBytes + 1));
            var error = BuildGate.OversizedSourceError(workspace);
            Assert.NotNull(error);
            Assert.Contains("Big.cs", error);
            Assert.Contains("error AP0001", error);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    [Fact]
    public void FilesUnderTheCapAreNotRejected()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(workspace, "Small.cs"), "class Small {}");
            Assert.Null(BuildGate.OversizedSourceError(workspace));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    // ---- error parsing ----

    [Fact]
    public void ParseErrorsExtractsAndDedupesErrorLines()
    {
        var output = "Build started\nFoo.cs(3,5): error CS0535: 'Foo' does not implement\n" +
                     "Foo.cs(3,5): error CS0535: 'Foo' does not implement\nBuild succeeded? no\n";
        var errors = BuildGate.ParseErrors(output);
        Assert.Single(errors);
        Assert.Contains("error CS0535", errors[0]);
    }

    [Fact]
    public void ParseErrorsIgnoresWarningsAndPlainOutput()
    {
        var output = "warning CS8600: possible null\nRestore complete.\n";
        Assert.Empty(BuildGate.ParseErrors(output));
    }

    // ---- sandboxed options (ruling 1: tmpfs, not the read-only /src mount, is the writable /build) ----

    [Fact]
    public void SandboxedOptionsDenyNetworkAndGrantOnlyAWritableTmpfsBuildDir()
    {
        var args = SandboxRunner.BuildRunArguments(BuildGate.SandboxedOptions("/host/ws"), ["dotnet"]).ToList();
        Assert.Equal("none", args[args.IndexOf("--network") + 1]);
        Assert.Contains("--tmpfs", args);
        Assert.Contains("/tmp:rw,exec,nosuid,nodev,size=1g", args);
        Assert.Contains("type=bind,src=/host/ws,dst=/src,readonly", args);
    }

    [Fact]
    public void SandboxedOptionsSetTheSameOfflineEnvironmentAsContainerCodeRunner()
    {
        // Tmpfs (and HOME/DOTNET_CLI_HOME) sit on /tmp, not a separate /build mount - see the
        // doc comment on SandboxedOptions for why /build alone fails (the CLI's first-run
        // mutex is hardcoded under /tmp/.dotnet/shm regardless of HOME/TMPDIR).
        var args = SandboxRunner.BuildRunArguments(BuildGate.SandboxedOptions("/host/ws"), ["dotnet"]);
        Assert.Contains("HOME=/tmp", args);
        Assert.Contains("DOTNET_CLI_HOME=/tmp/dotnet", args);
        Assert.Contains("DOTNET_NOLOGO=1", args);
        Assert.Contains("DOTNET_CLI_TELEMETRY_OPTOUT=1", args);
    }

    // ---- fail-closed / double opt-in, same shape as CodeRunnerFactory ----

    private static void WithBothVariables(string? enable, string? acknowledge, Action body) =>
        WithEnvironmentVariable(BuildGate.UnsafeEnableVariable, enable, () =>
        WithEnvironmentVariable(BuildGate.UnsafeAcknowledgementVariable, acknowledge, () => { body(); return true; }));

    [Fact]
    public void NeitherVariableSetMeansNoOptIn() =>
        WithBothVariables(null, null, () => Assert.False(BuildGate.IsUnsafeHostBuildRequested()));

    [Fact]
    public void EnableAloneIsInsufficient() =>
        WithBothVariables(BuildGate.UnsafeEnableValue, null, () => Assert.False(BuildGate.IsUnsafeHostBuildRequested()));

    [Fact]
    public void AcknowledgementAloneIsInsufficient() =>
        WithBothVariables(null, BuildGate.UnsafeAcknowledgementValue, () => Assert.False(BuildGate.IsUnsafeHostBuildRequested()));

    [Fact]
    public void BothVariablesTogetherOptIn() =>
        WithBothVariables(BuildGate.UnsafeEnableValue, BuildGate.UnsafeAcknowledgementValue,
            () => Assert.True(BuildGate.IsUnsafeHostBuildRequested()));

    [Fact]
    public void FailClosedMessageNamesBothOverrideVariables()
    {
        Assert.Contains(BuildGate.UnsafeEnableVariable, BuildGate.FailClosedMessage);
        Assert.Contains(BuildGate.UnsafeAcknowledgementVariable, BuildGate.FailClosedMessage);
    }

    // ---- C1: the exit code is never discarded - a nonzero exit with no parsed compiler
    // diagnostic must not read as a pass. Pure, so no Docker/host build needed to pin it. ----

    [Fact]
    public void NonzeroExitWithNoParsedErrorBecomesASyntheticGateError()
    {
        var result = new SandboxResult(1, "", "cp: cannot access '/src': Permission denied\n", TimedOut: false);
        var errors = BuildGate.InterpretResult(result);
        Assert.Single(errors);
        Assert.Contains("error AP0003", errors[0]);
        Assert.Contains("Permission denied", errors[0]);
    }

    [Fact]
    public void ZeroExitWithNoOutputIsAGenuinePass() =>
        Assert.Empty(BuildGate.InterpretResult(new SandboxResult(0, "", "", TimedOut: false)));

    [Fact]
    public void NonzeroExitWithAParsedCompilerErrorReportsOnlyTheCompilerError()
    {
        var result = new SandboxResult(1, "Foo.cs(1,1): error CS0535: whatever\n", "", TimedOut: false);
        var errors = BuildGate.InterpretResult(result);
        Assert.Single(errors);
        Assert.Contains("CS0535", errors[0]);
        Assert.DoesNotContain(errors, e => e.Contains("AP0003"));
    }

    [Fact]
    public void TimedOutTakesPriorityOverExitCode() =>
        Assert.Equal(["error AP0002: the build gate timed out"],
            BuildGate.InterpretResult(new SandboxResult(1, "", "", TimedOut: true)));

    // ---- C2: workspace/file permissions must not depend on the operator's umask ----

    [Fact]
    public void CreateWorkspaceDirectoryIsWorldReadableAndTraversable()
    {
        if (OperatingSystem.IsWindows()) return; // UnixFileMode is a no-op there
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        try
        {
            BuildGate.CreateWorkspaceDirectory(path);
            var mode = File.GetUnixFileMode(path);
            Assert.True(mode.HasFlag(UnixFileMode.OtherRead) && mode.HasFlag(UnixFileMode.OtherExecute));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task WriteWorldReadableAsyncMakesTheFileReadableByOthers()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, "f.cs");
            await BuildGate.WriteWorldReadableAsync(path, "class C {}", CancellationToken.None);
            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- host fallback: I3 wants a test that actually ENTERS HostBuildAsync, not one that
    // throws before the NuGet.config write even completes. Both below run a real `dotnet
    // build` on the host - no Docker needed, since this is the unsandboxed path. ----

    [Fact]
    public async Task HostFallbackActuallyCompilesOnTheHostAndReportsTheCompilerError()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(workspace, "Broken.cs"), "this is not C#;");
            File.WriteAllText(Path.Combine(workspace, "Broken.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                    </PropertyGroup>
                </Project>
                """);

            var errors = await BuildGate.RunAsync(workspace, useSandbox: false, CancellationToken.None);

            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains(": error "));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    [Fact]
    public async Task CancellationDuringHostBuildPropagatesInsteadOfHangingOrFalselyPassing()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(workspace, "Program.cs"), "System.Console.WriteLine(\"hi\");");
            File.WriteAllText(Path.Combine(workspace, "Program.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                    </PropertyGroup>
                </Project>
                """);
            // Cancels after the NuGet.config write but almost certainly while `dotnet build`
            // is still running (process startup alone dominates 50ms) - I3: this must
            // propagate as cancellation (not hang, not report a false PASSED/timeout).
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BuildGate.RunAsync(workspace, useSandbox: false, cts.Token));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }
}

// Live verification of the sandbox BOUNDARY itself: writes a workspace with the same shape
// Program.cs writes (contracts + a deliberately contract-breaking worker file) and runs the
// real gate through Docker. Needs Docker; on machines without it these pass vacuously rather
// than fail the suite, matching CodeActSandboxSmokeTests.
public class StigmergicBuildGateSandboxTests
{
    private static readonly bool DockerAvailable = SandboxRunner.IsAvailable("docker");

    private const string Contracts =
        """
        namespace Campaign;
        public record ProductSpec(string Name);
        public interface IPricingModule { int[] GetTiers(ProductSpec spec); }
        """;

    private static string CreateWorkspace()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(workspace, "Contracts.cs"), Contracts);
        // Mirrors Program.cs's IntegrationGate.cs: never executed, but instantiating
        // PricingModule where IPricingModule is expected is what actually forces the
        // compiler to check the contract - without this, an unrelated class just compiles.
        File.WriteAllText(Path.Combine(workspace, "IntegrationGate.cs"),
            "namespace Campaign;\npublic static class IntegrationGate { public static IPricingModule Wire() => new PricingModule(); }");
        File.WriteAllText(Path.Combine(workspace, "Campaign.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                </PropertyGroup>
            </Project>
            """);
        return workspace;
    }

    [Fact]
    public async Task ContractDriftIsCaughtInsideTheSandbox()
    {
        if (!DockerAvailable) return;

        var workspace = CreateWorkspace();
        try
        {
            // Deliberately does NOT implement IPricingModule - the same drift Program.cs seeds.
            File.WriteAllText(Path.Combine(workspace, "PricingModule.cs"),
                "namespace Campaign;\npublic sealed class PricingModule { public int[] GetPrices() => []; }");

            var errors = await BuildGate.RunAsync(workspace, useSandbox: true, CancellationToken.None);

            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains(": error "));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    [Fact]
    public async Task ConformingWorkerPassesTheSandboxedGateOffline()
    {
        if (!DockerAvailable) return;

        var workspace = CreateWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(workspace, "PricingModule.cs"),
                """
                namespace Campaign;
                public sealed class PricingModule : IPricingModule
                {
                    public int[] GetTiers(ProductSpec spec) => [10, 20, 30];
                }
                """);

            var errors = await BuildGate.RunAsync(workspace, useSandbox: true, CancellationToken.None);

            Assert.Empty(errors);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    // C1 regression, reproduced the same way the reviewer did: an artificially starved
    // pids-limit kills `dotnet build` (fork fails) before it ever prints a line containing
    // ": error " - verified by hand first ("sh: 1: Cannot fork", exit 2). InterpretResult
    // must not read that silence as PASSED.
    [Fact]
    public async Task PidsLimitKillIsNotSilentlyReadAsPassed()
    {
        if (!DockerAvailable) return;

        var workspace = CreateWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(workspace, "PricingModule.cs"),
                """
                namespace Campaign;
                public sealed class PricingModule : IPricingModule
                {
                    public int[] GetTiers(ProductSpec spec) => [10, 20, 30];
                }
                """);

            var options = BuildGate.SandboxedOptions(workspace) with { PidsLimit = 1 };
            var result = await SandboxRunner.RunAsync(options,
                ["sh", "-c", "cp -r /src /tmp/build && cd /tmp/build && dotnet build -nologo --verbosity quiet"],
                stdin: null, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.NotEmpty(BuildGate.InterpretResult(result));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }
}
