using Microsoft.SemanticKernel;

namespace GoalSettingsAndMonitoring.SemanticKernel;

public class GoalMonitoringFilter : IAutoFunctionInvocationFilter
{
    private const int MaxIterations = 5;
    private int _iteration;

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        await next(context);

        if (context.Function.Name != "EvaluateGoals") return;

        _iteration++;
        var result = context.Result.GetValue<GoalEvaluationResult>();

        Console.WriteLine($"[Monitor] Iteration {_iteration}/{MaxIterations}");

        if (result?.AllGoalsMet == true)
        {
            Console.WriteLine("[Monitor] Goals achieved — terminating loop.");
            context.Terminate = true;
            return;
        }

        if (_iteration >= MaxIterations)
        {
            Console.WriteLine("[Monitor] Max iterations reached — terminating with best effort.");
            context.Terminate = true;
        }
    }
}