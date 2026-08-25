using Shared.Sandbox;

namespace StigmergicCoordination.AgentFramework;

/// <summary>
/// The mechanical gate: `dotnet build` over the shared workspace, run inside the SAME
/// constrained-execution boundary CodeAct uses for model-generated code. Compiling untrusted
/// source is still running untrusted code — build tasks, source generators, and MSBuild
/// targets all execute as part of a build, not just at run time — so the workspace is
/// compiled in a locked-down container (no network, read-only source mount, bounded writable
/// build directory, capped CPU/memory/pids, wall-clock timeout, bounded output), never
/// directly on the host. See the repository's untrusted-execution rule.
/// </summary>
public static class BuildGate
{
    public const long MaxSourceBytes = 64 * 1024;
    private const string ContainerImage = "mcr.microsoft.com/dotnet/sdk:10.0";
    private const int MaxOutputCharacters = 65_536;

    // Same two variables CodeAct's CodeRunnerFactory reads (Execution/CodeRunnerFactory.cs) -
    // duplicated here rather than referenced across projects, since no sample in this repo
    // references another sample's project. Only THIS sample's fail-closed path offers them,
    // because only this sample actually reads them - unlike MCP, which has no host fallback.
    public const string UnsafeEnableVariable = "AGENTIC_PATTERNS_ALLOW_UNSAFE_HOST_EXECUTION";
    public const string UnsafeEnableValue = "true";
    public const string UnsafeAcknowledgementVariable = "AGENTIC_PATTERNS_ACKNOWLEDGE_UNSAFE_CODE_EXECUTION";
    public const string UnsafeAcknowledgementValue = "I_UNDERSTAND_THIS_RUNS_UNTRUSTED_CODE_ON_MY_HOST";

    private const string NuGetConfig =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
          </packageSources>
        </configuration>
        """;

    public static string FailClosedMessage =>
        $"""
        No container runtime available. This sample compiles model-generated C# files, which
        is untrusted code - build tasks, source generators, and MSBuild targets all run during
        a build, not just at execution. It will not be compiled on the host.

        Install Docker or Podman to run it, or explicitly opt into an unsandboxed host build
        (the build still gets a timeout and a source size cap, but none of the container
        isolation) with both:
          1. {UnsafeEnableVariable}={UnsafeEnableValue}
          2. {UnsafeAcknowledgementVariable}={UnsafeAcknowledgementValue}
        """;

    /// <summary>Double opt-in, same shape as CodeAct - one variable alone is never enough.</summary>
    public static bool IsUnsafeHostBuildRequested() =>
        Environment.GetEnvironmentVariable(UnsafeEnableVariable) == UnsafeEnableValue &&
        Environment.GetEnvironmentVariable(UnsafeAcknowledgementVariable) == UnsafeAcknowledgementValue;

    /// <summary>Oversized files never reach the compiler (sandboxed or not) - checked up front.</summary>
    public static string? OversizedSourceError(string workspace)
    {
        foreach (var file in Directory.GetFiles(workspace, "*.cs"))
            if (new FileInfo(file).Length > MaxSourceBytes)
                return $"{Path.GetFileName(file)}: error AP0001: source exceeds {MaxSourceBytes} bytes";
        return null;
    }

    public static List<string> ParseErrors(string combinedOutput) =>
        [.. combinedOutput.Split('\n').Where(l => l.Contains(": error ")).Select(l => l.Trim()).Distinct()];

    /// <summary>
    /// `SandboxOptions` defaults to `--read-only`, so copying the workspace out of the
    /// read-only `/src` mount needs a writable mount - a bounded tmpfs. The tmpfs (and HOME /
    /// DOTNET_CLI_HOME) land on `/tmp`, exactly where `ContainerCodeRunner` puts them - NOT on
    /// a separate `/build` tmpfs, which was tried first and measured to fail: the .NET CLI's
    /// interprocess "first run" mutex is hardcoded to `/tmp/.dotnet/shm`, ignoring HOME and
    /// TMPDIR, so a writable `/build` with a still-read-only `/tmp` fails on every run with
    /// "mkdir(/tmp/.dotnet/shm/session1) == -1; errno == EROFS" before the compiler ever sees
    /// the source. Mounting the tmpfs at `/tmp` is what `ContainerCodeRunner` already does, is
    /// verified working here, and keeps the same environment variables it passes: what lets
    /// the SDK run offline as a non-root user with no writable home.
    /// </summary>
    public static SandboxOptions SandboxedOptions(string workspace) => new(
        Image: ContainerImage,
        Network: false, Memory: "1g", Cpus: "2", PidsLimit: 256,
        Timeout: TimeSpan.FromMinutes(3),
        Tmpfs: "/tmp:rw,exec,nosuid,nodev,size=1g",
        Environment: new Dictionary<string, string>
        {
            ["HOME"] = "/tmp",
            ["DOTNET_CLI_HOME"] = "/tmp/dotnet",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        },
        Mounts: [(workspace, "/src", true)]);

    /// <summary>
    /// Runs the gate. <paramref name="useSandbox"/> is decided ONCE by the caller (before any
    /// worker runs) rather than re-checked every round, mirroring CodeAct's one-time runner
    /// selection - availability cannot silently change mid-run into a different security
    /// posture.
    /// </summary>
    public static async Task<List<string>> RunAsync(
        string workspace, bool useSandbox, CancellationToken cancellationToken)
    {
        var oversized = OversizedSourceError(workspace);
        if (oversized is not null) return [oversized];

        // Ruling 3: written every round (cheap, idempotent) so a restore that ever tries to
        // reach a feed fails loudly instead of silently depending on Network: false alone.
        await File.WriteAllTextAsync(Path.Combine(workspace, "NuGet.config"), NuGetConfig, cancellationToken);

        if (!useSandbox) return await HostBuildAsync(workspace, cancellationToken);

        var result = await SandboxRunner.RunAsync(SandboxedOptions(workspace),
            ["sh", "-c", "cp -r /src /tmp/build && cd /tmp/build && dotnet build -nologo --verbosity quiet"],
            stdin: null, cancellationToken);

        if (result.TimedOut) return ["error AP0002: the build gate timed out"];
        return ParseErrors(result.StdOut + result.StdErr);
    }

    // ponytail: unsandboxed - only reachable behind the double opt-in in IsUnsafeHostBuildRequested,
    // same shape as CodeAct's UnsafeHostCodeRunner. Upgrade path is the same: delete this method
    // once every environment running the sample has a container runtime.
    private static async Task<List<string>> HostBuildAsync(string workspace, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("dotnet", "build -nologo --verbosity quiet")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
        try
        {
            var stdoutTask = BoundedReader.ReadBoundedAsync(process.StandardOutput, MaxOutputCharacters, timeoutCts.Token);
            var stderrTask = BoundedReader.ReadBoundedAsync(process.StandardError, MaxOutputCharacters, timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return ParseErrors(await stdoutTask + await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return ["error AP0002: the build gate timed out"];
        }
    }
}
