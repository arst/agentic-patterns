using System.Diagnostics;
using Shared.Sandbox;

namespace CodeAct.AgentFramework.Execution;

/// <summary>
/// The default runner: executes model-generated code in a locked-down local container.
/// Least privilege throughout — the container gets NOTHING by default (no network, no
/// capabilities, no host filesystem, no host environment, no root) and only what a
/// compile-and-run of a BCL-only script strictly needs is granted back: a read-only
/// mount of the per-run script directory and a bounded tmpfs for build artifacts.
/// This demonstrates the required isolation boundary; it is NOT a production-grade
/// sandbox for adversarial or multi-tenant workloads (use a disposable VM/microVM
/// isolation service for that). The isolation boundary itself lives in
/// <see cref="SandboxRunner"/> (Shared.Sandbox) so other samples can reuse it; this
/// class owns only what is specific to CodeAct: the sandbox image, per-run script
/// staging, and mapping <see cref="CodeExecutionOptions"/> onto <see cref="SandboxOptions"/>.
/// </summary>
public sealed class ContainerCodeRunner(CodeExecutionOptions options) : IGeneratedCodeRunner
{
    private static readonly IReadOnlyList<string> RunScriptCommand = ["dotnet", "run", "/workspace/script.cs"];

    /// <summary>True when the runtime CLI exists AND its daemon answers.</summary>
    public static bool IsAvailable(string containerRuntime) => SandboxRunner.IsAvailable(containerRuntime);

    /// <summary>
    /// The whole security posture, as one pure function so tests can pin every flag.
    /// Deny everything; allow only what `dotnet run script.cs` needs. Thin mapping onto
    /// <see cref="SandboxRunner.BuildRunArguments"/> — the argument construction itself
    /// lives there now.
    /// </summary>
    public static IReadOnlyList<string> BuildRunArguments(
        string containerName, string runDirectory, CodeExecutionOptions options) =>
        SandboxRunner.BuildRunArguments(ToSandboxOptions(containerName, runDirectory, options), RunScriptCommand);

    private static SandboxOptions ToSandboxOptions(
        string containerName, string runDirectory, CodeExecutionOptions options) => new(
        Image: options.ContainerImage,
        ContainerRuntime: options.ContainerRuntime,
        Network: false,
        Memory: "1g",                                    // enough for Roslyn to compile, nothing runaway
        Cpus: "1",
        PidsLimit: 128,                                   // fork bombs die early
        Timeout: options.ExecutionTimeout,
        MaxOutputCharacters: options.MaxOutputCharacters,
        Environment: new Dictionary<string, string>       // no host env is forwarded; these four are the
        {                                                  // complete environment the SDK needs to run
            ["HOME"] = "/tmp",                             // as an unknown non-root user offline
            ["DOTNET_CLI_HOME"] = "/tmp/dotnet",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        },
        Mounts: [(runDirectory, "/workspace", true)],      // per-run dir only, read-only
        ContainerName: containerName,                      // unique name so a timeout can kill THIS container
        User: "65532:65532",                                // non-root, no matching user on the host
        Tmpfs: "/tmp:rw,exec,nosuid,nodev,size=512m");      // the ONLY writable path: bounded, for build
                                                             // artifacts; exec because the compiled script
                                                             // binary lives (and must run) here

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

            var sandboxOptions = ToSandboxOptions(containerName, runDirectory, options);
            var result = await SandboxRunner.RunAsync(sandboxOptions, RunScriptCommand, stdin: null, cancellationToken);
            return new ExecutionResult(result.ExitCode, result.StdOut, result.StdErr, result.TimedOut);
        }
        finally
        {
            try { Directory.Delete(runDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Task WriteWorldReadableAsync(string path, string content, CancellationToken cancellationToken) =>
        HostWorkspace.WriteWorldReadableAsync(path, content, cancellationToken);

    /// <summary>The per-run staging directory, world-traversable so container uid 65532 can
    /// read the bind mount regardless of the operator's umask - see <see cref="HostWorkspace"/>,
    /// which owns that fix for both this sample and StigmergicCoordination's build gate.</summary>
    internal static string CreateRunDirectory(string runId) =>
        HostWorkspace.CreateWorldReadableDirectory(Path.Combine(Path.GetTempPath(), "codeact", runId));

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

    // ponytail: duplicates SandboxRunner's private process-exec helper. `docker image
    // inspect`/`docker build` aren't sandboxed container runs, so SandboxRunner's public
    // surface (fixed by the task interface) has no method for them; a ~20-line local
    // helper is cheaper than adding a new public "run arbitrary runtime command" API
    // that nothing else needs yet. Promote to a shared helper if a third caller appears.
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunRuntimeCommandAsync(
        string containerRuntime, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var startInfo = new ProcessStartInfo(containerRuntime)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
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
