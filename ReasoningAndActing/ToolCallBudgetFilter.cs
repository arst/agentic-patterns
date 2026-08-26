using Microsoft.SemanticKernel;

namespace ReasoningAndActing;

/// <summary>
/// Hard bound on tool calls: the prompt asks the model to stop at 10, this enforces it.
/// </summary>
// ponytail: GuardAsync(Func<Task>) is a context-free stand-in for OnFunctionInvocationAsync so
// the counting/throwing logic is unit-testable. Upgrade path: SemanticKernel's
// FunctionInvocationContext constructor is internal, so a test can't build a real one - if a
// future SK version makes it public, this seam can be dropped and the test can drive
// OnFunctionInvocationAsync directly.
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
            throw new ToolCallBudgetExceededException(MaxToolCalls);

        await next();
    }

    public Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next) =>
        GuardAsync(() => next(context));
}
