using System.Diagnostics;

namespace CodeAct.AgentFramework.Execution;

/// <summary>
/// The default runner: executes model-generated code in a locked-down local container.
/// Least privilege throughout — the container gets NOTHING by default (no network, no
/// capabilities, no host filesystem, no host environment, no root) and only what a
/// compile-and-run of a BCL-only script strictly needs is granted back: a read-only
/// mount of the per-run script directory and a bounded tmpfs for build artifacts.
/// This demonstrates the required isolation boundary; it is NOT a production-grade
/// sandbox for adversarial or multi-tenant workloads (use a disposable VM/microVM
/// isolation service for that).
/// </summary>
public sealed class ContainerCodeRunner(CodeExecutionOptions options) : IGeneratedCodeRunner
{
    /// <summary>True when the runtime CLI exists AND its daemon answers.</summary>
    public static bool IsAvailable(string containerRuntime)
    {
        try
        {
            var (exitCode, _, _) = RunRuntimeCommandAsync(containerRuntime,
                ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            return exitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false; // CLI not on PATH
        }
    }

    /// <summary>
    /// The whole security posture, as one pure function so tests can pin every flag.
    /// Deny everything; allow only what `dotnet run script.cs` needs.
    /// </summary>
    public static IReadOnlyList<string> BuildRunArguments(
        string containerName, string runDirectory, CodeExecutionOptions options) =>
    [
        "run", "--rm",
        "--name", containerName,                        // unique name so a timeout can kill THIS container
        "--network", "none",                            // no network, not even DNS
        "--read-only",                                  // immutable container filesystem
        "--cap-drop", "ALL",                            // no Linux capabilities
        "--security-opt", "no-new-privileges=true",     // setuid binaries cannot escalate
        "--pids-limit", "128",                          // fork bombs die early
        "--memory", "1g",                               // enough for Roslyn to compile, nothing runaway
        "--cpus", "1",
        "--user", "65532:65532",                        // non-root, no matching user on the host
        "--tmpfs", "/tmp:rw,exec,nosuid,nodev,size=512m", // the ONLY writable path: bounded, for build
                                                        // artifacts; exec because the compiled script
                                                        // binary lives (and must run) here
        "--mount", $"type=bind,src={runDirectory},dst=/workspace,readonly", // per-run dir only, read-only
        "--env", "HOME=/tmp",                           // no host env is forwarded; these four are the
        "--env", "DOTNET_CLI_HOME=/tmp/dotnet",         // complete environment the SDK needs to run
        "--env", "DOTNET_NOLOGO=1",                     // as an unknown non-root user offline
        "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
        options.ContainerImage,
        "dotnet", "run", "/workspace/script.cs"
    ];

    public async Task<ExecutionResult> RunAsync(string sourceCode, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        var containerName = $"codeact-{runId}";
        var runDirectory = CreateRunDirectory(runId);
        try
        {
            await WriteWorldReadableAsync(Path.Combine(runDirectory, "script.cs"), sourceCode, cancellationToken);
            // No package feeds, stated explicitly — restore must resolve everything from
            // the offline cache baked into the image (see Sandbox/Dockerfile), so the
            // model cannot pull packages even if the network boundary ever regressed.
            await WriteWorldReadableAsync(Path.Combine(runDirectory, "NuGet.config"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                  </packageSources>
                </configuration>
                """, cancellationToken);

            await EnsureImageAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.ExecutionTimeout);

            using var process = StartRuntimeProcess(options.ContainerRuntime,
                BuildRunArguments(containerName, runDirectory, options));
            try
            {
                var stdoutTask = BoundedReader.ReadBoundedAsync(
                    process.StandardOutput, options.MaxOutputCharacters, timeoutCts.Token);
                var stderrTask = BoundedReader.ReadBoundedAsync(
                    process.StandardError, options.MaxOutputCharacters, timeoutCts.Token);

                await process.WaitForExitAsync(timeoutCts.Token);

                return new ExecutionResult(process.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout, not caller cancellation. Kill by NAME: cancelling the client
                // process does not guarantee the containerized process has stopped.
                await KillContainerAsync(containerName);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return new ExecutionResult(
                    ExitCode: -1,
                    StandardOutput: "",
                    StandardError: "Execution exceeded the configured time limit.",
                    TimedOut: true);
            }
            // Caller cancellation propagates as OperationCanceledException — it is never
            // converted into an ordinary failure result. The finally still cleans up.
        }
        finally
        {
            await RemoveContainerAsync(containerName);
            try { Directory.Delete(runDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WriteWorldReadableAsync(string path, string content, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                       UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static string CreateRunDirectory(string runId)
    {
        var path = Path.Combine(Path.GetTempPath(), "codeact", runId);
        if (OperatingSystem.IsWindows())
            return Directory.CreateDirectory(path).FullName;

        // World-readable/traversable so container uid 65532 can read the bind mount.
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codeact"), mode);
        return Directory.CreateDirectory(path, mode).FullName;
    }

    /// <summary>Builds the repo-controlled sandbox image from Sandbox/Dockerfile on first use.</summary>
    private async Task EnsureImageAsync(CancellationToken cancellationToken)
    {
        var (exitCode, _, _) = await RunRuntimeCommandAsync(options.ContainerRuntime,
            ["image", "inspect", options.ContainerImage], TimeSpan.FromSeconds(30), cancellationToken);
        if (exitCode == 0) return;

        var dockerfile = Path.Combine(AppContext.BaseDirectory, "Sandbox", "Dockerfile");
        Console.WriteLine($"  [sandbox] building image '{options.ContainerImage}' (first run, pulls the .NET SDK base image)...");
        var (buildExit, _, buildErr) = await RunRuntimeCommandAsync(options.ContainerRuntime,
            ["build", "-t", options.ContainerImage, "-f", dockerfile, Path.GetDirectoryName(dockerfile)!],
            TimeSpan.FromMinutes(10), cancellationToken);
        if (buildExit != 0)
            throw new InvalidOperationException(
                $"Failed to build the sandbox image '{options.ContainerImage}'. Build it manually with:\n" +
                $"  {options.ContainerRuntime} build -t {options.ContainerImage} CodeAct.AgentFramework/Sandbox\n{buildErr}");
    }

    private async Task KillContainerAsync(string containerName) =>
        await RunRuntimeCommandAsync(options.ContainerRuntime,
            ["kill", containerName], TimeSpan.FromSeconds(30));

    private async Task RemoveContainerAsync(string containerName)
    {
        // Belt and braces next to --rm; a missing container is the expected happy path.
        try
        {
            await RunRuntimeCommandAsync(options.ContainerRuntime,
                ["rm", "-f", containerName], TimeSpan.FromSeconds(30));
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static Process StartRuntimeProcess(string containerRuntime, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(containerRuntime)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo)!;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunRuntimeCommandAsync(
        string containerRuntime, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var process = StartRuntimeProcess(containerRuntime, arguments);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
