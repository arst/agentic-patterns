using Microsoft.SemanticKernel;

namespace ReasoningAndActing;

/// <summary>
/// Hard bound on tool calls: the prompt asks the model to stop at 10, this enforces it. The
/// counting/throwing logic lives in <see cref="GuardAsync"/>, a context-free core that a test can
/// drive directly — <see cref="FunctionInvocationContext"/>'s constructor is internal to Semantic
/// Kernel, so a test cannot build one to call <see cref="OnFunctionInvocationAsync"/> itself.
/// </summary>
public class ToolCallBudgetFilter : IFunctionInvocationFilter
{
    public const int MaxToolCalls = 10;

    private int _toolCalls;

    /// <summary>
    /// Increments the call counter and invokes <paramref name="next"/> only while under budget.
    /// Throws before calling <paramref name="next"/> once the budget is exhausted, so the call
    /// that would exceed it never runs.
    /// </summary>
    public async Task GuardAsync(Func<Task> next)
    {
        if (Interlocked.Increment(ref _toolCalls) > MaxToolCalls)
            throw new InvalidOperationException($"Tool-call budget of {MaxToolCalls} exhausted.");

        await next();
    }

    public Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next) =>
        GuardAsync(() => next(context));
}
