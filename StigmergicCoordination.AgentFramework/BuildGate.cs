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

    // ponytail: the stock SDK image, pulled from the network on first `docker run` - unlike
    // CodeAct's ContainerCodeRunner, which builds a repo-controlled image with an offline
    // package cache baked in (CodeAct.AgentFramework/Sandbox/Dockerfile). Same isolation
    // flags, different image provenance: a first-run pull failure is a real failure mode here
    // that CodeAct doesn't have. Upgrade path: bake a similar offline image if this sample
    // needs to run with zero host network access, including for the initial pull.
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
    /// C1: a nonzero exit with no parsed compiler diagnostic is NOT a pass. `cp` permission
    /// failures, a `--pids-limit` kill, a daemon hiccup, or an image-pull failure all exit
    /// nonzero without ever printing a line containing ": error " - discarding the exit code
    /// (as the pre-sandbox `BuildAsync` did) turns every one of those into a silent PASSED.
    /// Single source of truth for both the sandboxed and host-fallback paths.
    /// </summary>
    public static List<string> InterpretResult(SandboxResult result)
    {
        if (result.TimedOut) return ["error AP0002: the build gate timed out"];
        var errors = ParseErrors(result.StdOut + result.StdErr);
        if (errors.Count == 0 && result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            return [$"error AP0003: the build gate could not run (exit {result.ExitCode}): {detail.Trim()}"];
        }
        return errors;
    }

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
        // World-readable: this file rides the read-only /src bind mount into the sandbox too.
        await WriteWorldReadableAsync(Path.Combine(workspace, "NuGet.config"), NuGetConfig, cancellationToken);

        if (!useSandbox) return await HostBuildAsync(workspace, cancellationToken);

        var result = await SandboxRunner.RunAsync(SandboxedOptions(workspace),
            ["sh", "-c", "cp -r /src /tmp/build && cd /tmp/build && dotnet build -nologo --verbosity quiet"],
            stdin: null, cancellationToken);

        return InterpretResult(result);
    }

    /// <summary>
    /// C2: the container runs as uid 65532, an unrelated uid on the host. The default
    /// directory/file permissions `Directory.CreateDirectory`/`File.WriteAllText` produce
    /// depend on the caller's umask - restrictive enough (0700, common with `umask 077`) and
    /// the bind mount is unreadable to the sandbox, `cp` fails, and (pre-C1-fix) that silently
    /// read as PASSED. Verified by hand that `Directory.CreateDirectory(path, unixCreateMode)`
    /// ALONE is not enough: `mkdir()`'s mode argument is itself masked by the process umask
    /// (0077 in, 0700 out, even though 0775 was requested) - the same call
    /// `ContainerCodeRunner.CreateRunDirectory` makes, and the same gap. An explicit
    /// `File.SetUnixFileMode` afterwards is what actually forces the bits: `chmod()`, unlike
    /// `mkdir()`, is not subject to umask.
    /// </summary>
    public static string CreateWorkspaceDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return Directory.CreateDirectory(path).FullName;

        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, mode);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
        return path;
    }

    /// <summary>Mirrors <c>ContainerCodeRunner.WriteWorldReadableAsync</c> - every file written
    /// into the workspace has to be readable by uid 65532, not just the directory.</summary>
    public static async Task WriteWorldReadableAsync(string path, string content, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                       UnixFileMode.GroupRead | UnixFileMode.OtherRead);
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
            return InterpretResult(new SandboxResult(process.ExitCode, await stdoutTask, await stderrTask, TimedOut: false));
        }
        catch (OperationCanceledException)
        {
            // I3: kill on EITHER a timeout or caller cancellation - an unsandboxed host
            // `dotnet build` left running after this method returns/throws is an orphaned
            // process, not just a discarded result (contrast SandboxRunner's kill-by-name).
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(); // release file handles so the workspace can be deleted
            if (cancellationToken.IsCancellationRequested) throw; // caller cancellation stays caller cancellation
            return InterpretResult(new SandboxResult(-1, "", "", TimedOut: true));
        }
    }
}
