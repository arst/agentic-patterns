namespace Shared.Sandbox;

/// <summary>
/// The constrained-execution boundary shared by every sample that runs untrusted,
/// model-generated work: deny everything by default (no network, no capabilities, no
/// host filesystem, no host environment, no root) and grant back only what the caller
/// explicitly asks for via <see cref="Mounts"/> and <see cref="Environment"/>.
/// </summary>
public sealed record SandboxOptions(
    string Image,
    string ContainerRuntime = "docker",
    bool Network = false,
    string Memory = "512m",
    string Cpus = "1",
    int PidsLimit = 128,
    TimeSpan Timeout = default,
    int MaxOutputCharacters = 65_536,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<(string Host, string Container, bool ReadOnly)>? Mounts = null,
    string? ContainerName = null,
    string? User = "65532:65532",
    string? Tmpfs = null,
    bool Interactive = false);

public sealed record SandboxResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
