using System.Diagnostics;

namespace CodeAct.AgentFramework.Execution;

[Obsolete(
    "UnsafeHostCodeRunner executes model-generated code on the host. " +
    "Use ContainerCodeRunner or a production isolation service.",
    error: false)]
public sealed class UnsafeHostCodeRunner(CodeExecutionOptions options) : IGeneratedCodeRunner
{
    public async Task<ExecutionResult> RunAsync(string sourceCode, CancellationToken cancellationToken)
    {
        var runDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "codeact", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "script.cs"), sourceCode, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.ExecutionTimeout);

            // SECURITY WARNING:
            // This intentionally unsafe implementation runs model-generated code on
            // the application host, with this process's user, environment, filesystem
            // and network. Never use this executor in a deployed system.
            using var process = Process.Start(new ProcessStartInfo("dotnet", "run script.cs")
            {
                WorkingDirectory = runDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            try
            {
                var stdoutTask = BoundedReader.ReadBoundedAsync(
                    process.StandardOutput, options.MaxOutputCharacters, timeoutCts.Token);
                var stderrTask = BoundedReader.ReadBoundedAsync(
                    process.StandardError, options.MaxOutputCharacters, timeoutCts.Token);

                await process.WaitForExitAsync(timeoutCts.Token);

                return new ExecutionResult(process.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(); // release file handles so the run directory can be deleted
                if (cancellationToken.IsCancellationRequested) throw; // caller cancellation stays caller cancellation
                return new ExecutionResult(
                    ExitCode: -1,
                    StandardOutput: "",
                    StandardError: "Execution exceeded the configured time limit.",
                    TimedOut: true);
            }
        }
        finally
        {
            try { Directory.Delete(runDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
