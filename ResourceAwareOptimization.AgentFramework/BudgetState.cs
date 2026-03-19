using Microsoft.Extensions.AI;

internal class BudgetState(double maxBudgetCents)
{
    private double _totalCostCents;
    public double TotalCostCents => _totalCostCents;
    public bool Exceeded => _totalCostCents >= maxBudgetCents;

    public void RecordUsage(string modelId, ChatResponse response)
    {
        if (response.Usage is { } usage)
        {
            // Approximate cost per 1K tokens
            var costPer1K = modelId switch
            {
                "gpt-4o-mini" => 0.015,
                "o4-mini" => 1.10,
                _ => 0.25
            };
            var totalTokens = (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);
            var cost = totalTokens / 1000.0 * costPer1K;
            Interlocked.Exchange(ref _totalCostCents, _totalCostCents + cost);
            Console.WriteLine(
                $"  [Budget] {modelId}: {totalTokens} tokens, " +
                $"~{cost:F3}¢ (total: {_totalCostCents:F2}¢ / {maxBudgetCents}¢)");
        }
    }
}