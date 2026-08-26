using Microsoft.SemanticKernel;

namespace ReasoningAndActing;

/// <summary>
/// Hard bound on tool calls: the prompt asks the model to stop at 10, this enforces it.
/// </summary>
/// <remarks>
/// This must be an <see cref="IAutoFunctionInvocationFilter"/> setting <c>context.Terminate</c>,
/// not an <see cref="IFunctionInvocationFilter"/> that throws. SK 1.79's
/// <c>FunctionCallsProcessor.ExecuteFunctionCallAsync</c> wraps every auto-invoked call in a
/// catch-all that turns any exception into a tool-result error message and keeps looping, so a
/// throwing filter blocks the tool body but not the loop — and hands the model the refusal to
/// paraphrase. <c>Terminate</c> is the only stop SK honours; see
/// <c>GoalSettingsAndMonitoring.SemanticKernel.GoalMonitoringFilter</c> for the same mechanism.
///
/// The counter lives on the instance, so the budget is per filter instance: register one instance
/// per run (Program.cs does), not the type as a process-wide singleton.
/// </remarks>
public class ToolCallBudgetFilter : IAutoFunctionInvocationFilter
{
    public const int MaxToolCalls = 10;

    private int _toolCalls;

    /// <summary>True once a call was refused because the budget was exhausted. SK returns
    /// normally after <c>Terminate</c>, so this flag is the only route the stop has out.</summary>
    public bool BudgetExhausted { get; private set; }

    public string StopReason => $"Tool-call budget of {MaxToolCalls} exhausted.";

    /// <summary>
    /// Counts every auto-invoked tool call and passes it through while under budget. The call that
    /// would exceed the budget never reaches <paramref name="next"/>, and terminates the
    /// auto-invocation loop instead of feeding the model an error to reason about.
    /// </summary>
    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        if (Interlocked.Increment(ref _toolCalls) > MaxToolCalls)
        {
            BudgetExhausted = true;
            context.Terminate = true;
            return;
        }

        await next(context);
    }
}
