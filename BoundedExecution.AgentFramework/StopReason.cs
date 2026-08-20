namespace BoundedExecution.AgentFramework;

public enum StopReason
{
    IterationLimitReached,
    ModelCallLimitReached,
    ToolCallLimitReached,
    InputTokenLimitReached,
    OutputTokenLimitReached,
    ElapsedTimeLimitReached,
    EstimatedCostLimitReached
}

public enum RunStatus { Complete, Partial }

public sealed record BoundedRunResult(RunStatus Status, string? Answer, StopReason? StopReason, BudgetSnapshot Budget);
