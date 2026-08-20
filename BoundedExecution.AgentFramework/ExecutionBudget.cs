namespace BoundedExecution.AgentFramework;

public sealed record ExecutionBudget(
    int MaxIterations,
    int MaxModelCalls,
    int MaxToolCalls,
    long MaxInputTokens,
    long MaxOutputTokens,
    TimeSpan MaxElapsedTime,
    decimal MaxEstimatedCost,
    decimal SoftThreshold = 0.8m);

public sealed record BudgetSnapshot(
    int Iterations,
    int ModelCalls,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    TimeSpan Elapsed,
    decimal EstimatedCost,
    bool SoftThresholdReached);
