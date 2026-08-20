namespace CodeAct.AgentFramework.Execution;

public sealed record CodeExecutionOptions
{
    /// <summary>"docker" or "podman".</summary>
    public string ContainerRuntime { get; init; } = "docker";

    /// <summary>Repo-controlled sandbox image, built from Sandbox/Dockerfile on first use.</summary>
    public string ContainerImage { get; init; } = "agentic-patterns-codeact-sandbox";

    /// <summary>
    /// First of the two required opt-ins for host execution (the CLI flag). The second is
    /// the acknowledgement environment variable — see <see cref="CodeRunnerFactory"/>.
    /// </summary>
    public bool AllowUnsafeHostExecution { get; init; }

    /// <summary>Generous because the sandboxed `dotnet run script.cs` includes compilation.</summary>
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Output retained per stream; the rest is drained and discarded so a looping
    /// `Console.WriteLine` cannot grow host memory without bound.</summary>
    public int MaxOutputCharacters { get; init; } = 64_000;
}
