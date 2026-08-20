namespace OrchestratorWorkers.AgentFramework;

public sealed record WorkPlan(IReadOnlyList<WorkerTask> Tasks);
