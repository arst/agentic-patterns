namespace OrchestratorWorkers.AgentFramework;

public sealed record WorkerTask(string Id, string Worker, string Instruction);

public sealed record WorkerResult(string TaskId, string Worker, string? Output, string? Error)
{
    public bool Succeeded => Error is null;
}
