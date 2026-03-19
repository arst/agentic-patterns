internal class AgentTelemetry
{
    private readonly List<CallRecord> _calls = [];
    private readonly List<TrajectoryRecord> _trajectories = [];

    public IReadOnlyList<CallRecord> Calls => _calls;
    public IReadOnlyList<TrajectoryRecord> Trajectories => _trajectories;

    public void RecordCall(string model, double latencyMs, long inputTokens, long outputTokens)
    {
        _calls.Add(new CallRecord(model, latencyMs, inputTokens, outputTokens, DateTime.UtcNow));
    }

    public void RecordTrajectory(string query, string response, double totalMs, int callCount)
    {
        _trajectories.Add(new TrajectoryRecord(query, response, totalMs, callCount, DateTime.UtcNow));
    }

    public void PrintSummary()
    {
        Console.WriteLine("\n═══ Telemetry Summary ═══");
        Console.WriteLine($"Total LLM calls: {_calls.Count}");
        Console.WriteLine($"Total tokens: {_calls.Sum(c => c.InputTokens + c.OutputTokens)}");
        Console.WriteLine($"Avg latency: {_calls.Average(c => c.LatencyMs):F0}ms");
        Console.WriteLine($"Total cost estimate: " +
                          $"{_calls.Sum(c => (c.InputTokens + c.OutputTokens) / 1000.0 * 0.25):F3}¢");

        Console.WriteLine($"\nTrajectories: {_trajectories.Count}");
        foreach (var t in _trajectories)
            Console.WriteLine($"  [{t.TotalLatencyMs:F0}ms, {t.LlmCallCount} calls] " +
                              $"Q: {t.UserQuery[..Math.Min(60, t.UserQuery.Length)]}...");
    }

    public record CallRecord(
        string Model,
        double LatencyMs,
        long InputTokens,
        long OutputTokens,
        DateTime Timestamp);

    public record TrajectoryRecord(
        string UserQuery,
        string AgentResponse,
        double TotalLatencyMs,
        int LlmCallCount,
        DateTime Timestamp);
}