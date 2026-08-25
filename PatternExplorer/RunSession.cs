using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace PatternExplorer;

/// <param name="S">Stream tag: out | err | in | sys.</param>
/// <param name="T">Raw text chunk (not line-buffered, so `Console.Write` prompts show up).</param>
public record Chunk(string S, string T);

/// Runs one sample as a child `dotnet run` and streams its console output. One instance per run,
/// looked up by (id, token) so concurrent tabs don't fight over a single global.
public sealed class RunSession
{
    // ponytail: a dictionary keyed by run id, capped at MaxLiveRuns and guarded by a single lock
    // so the count-then-add is actually atomic. A single-user local tool does not need a
    // scheduler; it does need two tabs not to fight. Upgrade to a real queue/scheduler if
    // Explorer ever grows multi-user or needs to run more than a handful of samples at once.
    const int MaxLiveRuns = 8;
    static readonly ConcurrentDictionary<string, RunSession> Runs = new(StringComparer.Ordinal);
    static readonly object RunsLock = new();

    static readonly TimeSpan MaxRuntime = TimeSpan.FromMinutes(15);
    const long MaxOutputBytes = 4 * 1024 * 1024;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    readonly Channel<Chunk> _channel = Channel.CreateBounded<Chunk>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly CancellationTokenSource _cts = new();
    readonly List<Process> _processes = [];
    StreamWriter? _input;
    long _outputBytes;
    int _outputLimitHit;

    public ChannelReader<Chunk> Reader => _channel.Reader;

    // Test seam: lets tests write chunks and observe drop-oldest behavior without spawning a process.
    internal ChannelWriter<Chunk> Writer => _channel.Writer;

    // Test seam: cancellation is otherwise unobservable from outside the session. Named IsCancelled
    // (not CancellationToken) so `CancellationToken.None` below keeps meaning the BCL type.
    internal bool IsCancelled => _cts.IsCancellationRequested;

    public static RunSession? TryGet(string id, string token) =>
        Runs.TryGetValue(id, out var session) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(session.Token), Encoding.UTF8.GetBytes(token))
            ? session : null;

    public static RunSession Start(string repoRoot, PatternProject project)
    {
        var session = new RunSession();
        Register(session);
        _ = Task.Run(() => session.RunAsync(repoRoot, project));
        return session;
    }

    // Registration and removal are split out from Start/RunAsync so tests can exercise session
    // lifetime (lookup, isolation, the live-run cap) without ever spawning `dotnet run`.
    internal static void Register(RunSession session)
    {
        lock (RunsLock)
        {
            if (Runs.Count >= MaxLiveRuns)
                throw new InvalidOperationException($"Too many live runs (max {MaxLiveRuns}). Cancel one and retry.");
            Runs[session.Id] = session;
        }
    }

    internal static void Unregister(RunSession session) => Runs.TryRemove(session.Id, out _);

    async Task RunAsync(string repoRoot, PatternProject project)
    {
        try
        {
            _cts.CancelAfter(MaxRuntime);

            if (project.Server is not null)
            {
                Emit("sys", $"dotnet run --project {project.Server}");
                StartProcess(repoRoot, project, project.Server, "out");
                await WaitForPortAsync(project.ServerPort, _cts.Token);
                Emit("sys", $"server is listening on port {project.ServerPort}");
            }

            Emit("sys", $"dotnet run --project {project.Path}");
            var main = StartProcess(repoRoot, project, project.Path, "out");
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
            Unregister(this);
        }
    }

    Process StartProcess(string repoRoot, PatternProject project, string projectPath, string tag)
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

        ApplyChildEnvironment(info.Environment, project);

        var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet run did not start.");
        lock (_processes) _processes.Add(process);

        _ = PumpAsync(process.StandardOutput, tag);
        _ = PumpAsync(process.StandardError, "err");
        return process;
    }

    /// The sample gets only what it needs to run, not Explorer's whole environment (which may hold
    /// credentials for other tools). `dotnet run` itself needs PATH/HOME/DOTNET_*.
    /// Test seam: the allowlist is the entire point of the child-process isolation, and
    /// StartProcess is otherwise only reachable through Start/RunAsync, which spawns a real
    /// `dotnet run`. Taking the dictionary lets a test assert the computed child environment
    /// without a process (see RunSessionTests).
    internal static void ApplyChildEnvironment(IDictionary<string, string?> environment, PatternProject project)
    {
        environment.Clear();
        foreach (var name in HostEnvironmentNamesForDotnetRun())
            CopyIfSet(environment, name);
        foreach (var name in project.EnvironmentAllowlist)
            CopyIfSet(environment, name);
    }

    // ponytail: PATH/HOME/DOTNET_* is what `dotnet run` needs on Linux/macOS, which is all this
    // repo targets (see README). Windows would also need USERPROFILE/APPDATA/SystemRoot/TEMP -
    // add them here if Explorer ever needs to run there.
    static IEnumerable<string> HostEnvironmentNamesForDotnetRun() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name is "PATH" or "HOME" || name.StartsWith("DOTNET_", StringComparison.Ordinal));

    static void CopyIfSet(IDictionary<string, string?> environment, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is not null) environment[name] = value;
    }

    // Character-level, not line-level: `Console.Write("Approve? (y/n): ")` must reach the browser
    // before the sample blocks on Console.ReadLine.
    async Task PumpAsync(StreamReader reader, string tag)
    {
        var buffer = new char[1024];
        while (true)
        {
            if (Volatile.Read(ref _outputLimitHit) != 0) return;

            var count = await reader.ReadAsync(buffer);
            if (count == 0) return;

            if (Interlocked.Add(ref _outputBytes, Encoding.UTF8.GetByteCount(buffer, 0, count)) > MaxOutputBytes)
            {
                if (Interlocked.Exchange(ref _outputLimitHit, 1) == 0)
                {
                    Emit("sys", "output limit reached, run cancelled");
                    _cts.Cancel();
                }
                return;
            }

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
