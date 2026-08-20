namespace CodeAct.AgentFramework.Execution;

public sealed record ExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);
