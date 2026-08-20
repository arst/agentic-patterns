using Microsoft.Extensions.AI;

namespace ResourceAwareOptimization.AgentFramework;

internal static class QueryRouter
{
    public static string Classify(IEnumerable<ChatMessage> messages)
    {
        var lastUserMsg = messages.LastOrDefault(m => m.Role == ChatRole.User);
        var text = lastUserMsg?.Text ?? "";
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        if (wordCount > 50
            || text.Contains("step by step", StringComparison.OrdinalIgnoreCase)
            || text.Contains("analyze", StringComparison.OrdinalIgnoreCase)
            || text.Contains("compare", StringComparison.OrdinalIgnoreCase))
            return "reasoning";

        return "simple";
    }

    /// <summary>
    /// Soft budget: only work that needs the expensive tier is refused once the budget
    /// is exceeded — simple queries still go through and the router forces the fast tier.
    /// </summary>
    public static bool RefuseForBudget(IEnumerable<ChatMessage> messages, bool budgetExceeded) =>
        budgetExceeded && Classify(messages) == "reasoning";
}
