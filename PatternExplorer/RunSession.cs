using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace PatternExplorer;

/// <param name="S">Stream tag: out | err | in | sys.</param>
/// <param name="T">Raw text chunk (not line-buffered, so `Console.Write` prompts show up).</param>
public record Chunk(string S, string T);

/// Runs one sample at a time as a child `dotnet run` and streams its console output.
public sealed class RunSession
{
    // ponytail: one run at a time, so a static current session is all the bookkeeping needed.
    public static RunSession? Current { get; private set; }

    readonly Channel<Chunk> _channel = Channel.CreateUnbounded<Chunk>();
    readonly CancellationTokenSource _cts = new();
    readonly List<Process> _processes = [];
    StreamWriter? _input;

    public ChannelReader<Chunk> Reader => _channel.Reader;

    public static RunSession Start(string repoRoot, PatternProject project)
    {
        Current?.Cancel();
        var session = new RunSession();
        Current = session;
        _ = Task.Run(() => session.RunAsync(repoRoot, project));
        return session;
    }

    async Task RunAsync(string repoRoot, PatternProject project)
    {
        try
        {
            if (project.Server is not null)
            {
                Emit("sys", $"dotnet run --project {project.Server}");
                StartProcess(repoRoot, project.Server, "out");
                await WaitForPortAsync(project.ServerPort, _cts.Token);
                Emit("sys", $"server is listening on port {project.ServerPort}");
            }

            Emit("sys", $"dotnet run --project {project.Path}");
            var main = StartProcess(repoRoot, project.Path, "out");
            _input = main.StandardInput;

            await main.WaitForExitAsync(_cts.Token);
            await Task.Delay(100, CancellationToken.None); // let the last output chunks drain
            Emit("sys", $"process exited with code {main.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            Emit("sys", "run cancelled");
        }
        catch (Exception ex)
        {
            Emit("sys", $"failed to run: {ex.Message}");
        }
        finally
        {
            KillAll();
            _channel.Writer.TryComplete();
        }
    }

    Process StartProcess(string repoRoot, string projectPath, string tag)
    {
        var directory = Path.Combine(repoRoot, projectPath);
        var info = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "run", "--project", directory },
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet run did not start.");
        lock (_processes) _processes.Add(process);

        _ = PumpAsync(process.StandardOutput, tag);
        _ = PumpAsync(process.StandardError, "err");
        return process;
    }

    // Character-level, not line-level: `Console.Write("Approve? (y/n): ")` must reach the browser
    // before the sample blocks on Console.ReadLine.
    async Task PumpAsync(StreamReader reader, string tag)
    {
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0) return;
            Emit(tag, new string(buffer, 0, count));
        }
    }

    static async Task WaitForPortAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("localhost", port, timeout.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250, timeout.Token);
            }
        }
    }

    public void SendInput(string text)
    {
        Emit("in", text + "\n");
        _input?.WriteLine(text);
        _input?.Flush();
    }

    public void Cancel()
    {
        _cts.Cancel();
        KillAll();
    }

    void KillAll()
    {
        lock (_processes)
        {
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { /* already gone */ }
            }
        }
    }

    void Emit(string stream, string text) =>
        _channel.Writer.TryWrite(new Chunk(stream, stream == "sys" ? text + "\n" : text));
}
