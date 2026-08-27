namespace BoundedExecution.AgentFramework;

/// <summary>
/// One run's limits. The <c>Max*</c> names are HARD ceilings the host can actually enforce: it
/// counts iterations, model calls and tool calls itself before dispatching, bounds elapsed time
/// with linked cancellation, and caps the provider's own <c>MaxOutputTokens</c> to what remains.
///
/// The two <c>*Budget</c> names are deliberately NOT called <c>Max…</c>, because the host cannot
/// promise them. Input tokens are estimated at ~4 chars/token before dispatch and only counted
/// for real on reconcile — after the provider has already read (and billed) the request — and
/// cost is derived from that same estimate. A sufficiently unusual input therefore lands slightly
/// over the number configured here and is caught one call late, which is a detector, not a
/// guarantee. Naming them apart is the point: a ceiling you enforce and a budget you reconcile
/// against are different promises, and the type should not let a caller confuse them.
/// </summary>
public sealed record ExecutionBudget(
    int MaxIterations,
    int MaxModelCalls,
    int MaxToolCalls,
    long InputTokenBudget,
    long MaxOutputTokens,
    TimeSpan MaxElapsedTime,
    decimal EstimatedCostBudget,
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
