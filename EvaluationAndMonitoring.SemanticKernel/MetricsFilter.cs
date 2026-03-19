using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.SemanticKernel;

internal class MetricsFilter(
    Histogram<double> latency,
    Counter<long> tokens,
    Counter<long> calls,
    ActivitySource activitySource) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var sw = Stopwatch.StartNew();
        using var activity = activitySource.StartActivity(
            $"agent.invoke.{context.Function.Name}");

        calls.Add(1, new KeyValuePair<string, object?>("function", context.Function.Name));

        await next(context);

        sw.Stop();
        latency.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("function", context.Function.Name));

        // Extract token usage from result metadata if available
        if (context.Result.Metadata?.TryGetValue("Usage", out var usage) == true)
            activity?.SetTag("gen_ai.tokens", usage?.ToString());
        // In production, parse CompletionsUsage for exact counts
        Console.WriteLine(
            $"  [Metrics] {context.Function.Name}: {sw.Elapsed.TotalMilliseconds:F1}ms");
    }
}