using Microsoft.SemanticKernel;

namespace ResourceAwareOptimization.SemanticKernel;

internal class BudgetTracker(int maxBudgetCents, double totalCostCents) : IFunctionInvocationFilter
{
    // Approximate cost per 1K tokens (input + output averaged)
    private static readonly Dictionary<string, double> CostPer1KTokens = new()
    {
        ["gpt-4o-mini"] = 0.015, // very cheap
        ["gpt-4o"] = 0.25, // mid-range
        ["o4-mini"] = 1.10 // expensive reasoning
    };

    public double TotalCostCents => totalCostCents;
    public bool BudgetExceeded => totalCostCents >= maxBudgetCents;

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        await next(context);

        // Extract token usage from the result metadata if available
        if (context.Result.Metadata?.TryGetValue("Usage", out var usage) == true)
        {
            // Estimate cost from token count
            var totalTokens = usage?.ToString();
            Console.WriteLine($"  [Budget] Tokens used: {totalTokens}");
        }
    }
}