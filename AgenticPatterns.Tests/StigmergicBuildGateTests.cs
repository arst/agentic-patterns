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

    // ---- host fallback wiring: caller cancellation must not be reported as a timeout ----

    [Fact]
    public async Task CallerCancellationDuringHostBuildIsNotConvertedIntoATimeoutResult()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BuildGate.RunAsync(workspace, useSandbox: false, new CancellationToken(canceled: true)));
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
}
