using Microsoft.SemanticKernel;
using OpenAI.Chat;

namespace ResourceAwareOptimization.SemanticKernel;

internal class BudgetTracker(int maxBudgetCents)
{
    // Approximate cost per 1K tokens (input + output averaged)
    private static readonly Dictionary<string, double> CostPer1KTokens = new()
    {
        ["gpt-4o-mini"] = 0.015, // very cheap
        ["gpt-4o"] = 0.25, // mid-range
        ["o4-mini"] = 1.10 // expensive reasoning
    };

    public double TotalCostCents { get; private set; }
    public bool BudgetExceeded => TotalCostCents >= maxBudgetCents;

    public void Record(string model, Microsoft.SemanticKernel.ChatMessageContent response)
    {
        // The AzureOpenAI connector exposes token usage under the "Usage" metadata key
        if (response.Metadata?.TryGetValue("Usage", out var usage) != true
            || usage is not ChatTokenUsage tokens)
            return;

        var cost = tokens.TotalTokenCount / 1000.0 * CostPer1KTokens.GetValueOrDefault(model);
        TotalCostCents += cost;
        Console.WriteLine(
            $"  [Budget] {tokens.TotalTokenCount} tokens on {model}: +{cost:F3}¢ (total {TotalCostCents:F2}¢ / {maxBudgetCents}¢)");
    }
}
