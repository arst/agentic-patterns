namespace BoundedExecution.AgentFramework;

public sealed class BudgetExceededException(StopReason reason, BudgetSnapshot snapshot)
    : Exception($"Execution stopped: {reason}")
{
    public StopReason Reason { get; } = reason;
    public BudgetSnapshot Snapshot { get; } = snapshot;
}
