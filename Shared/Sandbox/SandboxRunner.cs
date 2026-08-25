using System.Diagnostics;

namespace Shared.Sandbox;

/// <summary>
/// Runs a command inside a locked-down local container. This is the constrained-execution
/// boundary itself: least privilege throughout, nothing granted back except what an
/// individual <see cref="SandboxOptions"/> explicitly asks for. It demonstrates the required
/// isolation boundary; it is NOT a production-grade sandbox for adversarial or multi-tenant
/// workloads (use a disposable VM/microVM isolation service for that).
/// </summary>
public static class SandboxRunner
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
    /// Deny everything by default; grant back only what <paramref name="options"/> asks for.
    /// </summary>
    public static IReadOnlyList<string> BuildRunArguments(SandboxOptions options, IReadOnlyList<string> command)
    {
        List<string> args = ["run", "--rm"];

        if (options.ContainerName is not null)
        {
            args.Add("--name");
            args.Add(options.ContainerName);
        }
        if (options.Interactive) args.Add("-i");

        if (!options.Network)
        {
            args.Add("--network");
            args.Add("none");
        }

        args.Add("--read-only");
        args.Add("--cap-drop");
        args.Add("ALL");
        args.Add("--security-opt");
        args.Add("no-new-privileges=true");
        args.Add("--pids-limit");
        args.Add(options.PidsLimit.ToString());
        args.Add("--memory");
        args.Add(options.Memory);
        args.Add("--cpus");
        args.Add(options.Cpus);

        if (options.User is not null)
        {
            args.Add("--user");
            args.Add(options.User);
        }

        if (options.Tmpfs is not null)
        {
            args.Add("--tmpfs");
            args.Add(options.Tmpfs);
        }

        if (options.Mounts is not null)
        {
            foreach (var (host, container, readOnly) in options.Mounts)
            {
                args.Add("--mount");
                args.Add($"type=bind,src={host},dst={container}" + (readOnly ? ",readonly" : ""));
            }
        }

        if (options.Environment is not null)
        {
            foreach (var (name, value) in options.Environment)
            {
                args.Add("--env");
                args.Add($"{name}={value}");
            }
        }

        args.Add(options.Image);
        args.AddRange(command);
        return args;
    }

    /// <summary>
    /// Runs <paramref name="command"/> inside the sandbox and returns its output, bounded
    /// per <see cref="SandboxOptions.MaxOutputCharacters"/>. On timeout, kills the container
    /// by name (cancelling the client process does not guarantee the containerized process
    /// has stopped) and reports <see cref="SandboxResult.TimedOut"/> instead of throwing.
    /// Caller cancellation is never converted into a timeout result — it propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<SandboxResult> RunAsync(
        SandboxOptions options, IReadOnlyList<string> command, string? stdin, CancellationToken cancellationToken)
    {
        using var process = StartRuntimeProcess(options.ContainerRuntime,
            BuildRunArguments(options, command), redirectStandardInput: stdin is not null);
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // default(TimeSpan) means "no timeout configured", not "cancel immediately".
            if (options.Timeout > TimeSpan.Zero) timeoutCts.CancelAfter(options.Timeout);

            try
            {
                var stdoutTask = BoundedReader.ReadBoundedAsync(
                    process.StandardOutput, options.MaxOutputCharacters, timeoutCts.Token);
                var stderrTask = BoundedReader.ReadBoundedAsync(
                    process.StandardError, options.MaxOutputCharacters, timeoutCts.Token);

                await process.WaitForExitAsync(timeoutCts.Token);

                return new SandboxResult(process.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout, not caller cancellation.
                if (options.ContainerName is not null) await KillContainerAsync(options.ContainerRuntime, options.ContainerName);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return new SandboxResult(
                    ExitCode: -1,
                    StdOut: "",
                    StdErr: "Execution exceeded the configured time limit.",
                    TimedOut: true);
            }
            // Caller cancellation propagates as OperationCanceledException — it is never
            // converted into an ordinary failure result. The finally still cleans up.
        }
        finally
        {
            if (options.ContainerName is not null) await RemoveContainerAsync(options.ContainerRuntime, options.ContainerName);
        }
    }

    private static async Task KillContainerAsync(string containerRuntime, string containerName) =>
        await RunRuntimeCommandAsync(containerRuntime, ["kill", containerName], TimeSpan.FromSeconds(30));

    private static async Task RemoveContainerAsync(string containerRuntime, string containerName)
    {
        // Belt and braces next to --rm; a missing container is the expected happy path.
        try
        {
            await RunRuntimeCommandAsync(containerRuntime, ["rm", "-f", containerName], TimeSpan.FromSeconds(30));
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static Process StartRuntimeProcess(
        string containerRuntime, IReadOnlyList<string> arguments, bool redirectStandardInput = false)
    {
        var startInfo = new ProcessStartInfo(containerRuntime)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput
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
