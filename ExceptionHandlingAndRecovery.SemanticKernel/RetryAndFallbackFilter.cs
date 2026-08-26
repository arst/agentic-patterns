using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace ExceptionHandlingAndRecovery.SemanticKernel;

public class RetryAndFallbackFilter : IFunctionInvocationFilter
{
    private const int MaxRetries = 3;
    private readonly ILogger<RetryAndFallbackFilter> _logger;

    public RetryAndFallbackFilter(ILogger<RetryAndFallbackFilter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Whole-turn retry replays every tool call the turn made. Only use it for turns whose tools are
    /// read-only or idempotent; a turn that issues a refund must retry at the tool boundary with an
    /// idempotency key instead (see IdempotentToolCalls).
    /// </summary>
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var functionName = context.Function.Name;

        // Only apply retry/fallback logic to the primary lookup
        if (functionName != "GetPreciseLocation")
        {
            await next(context);
            return;
        }

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
            try
            {
                _logger.LogInformation(
                    "[ErrorDetection] Attempt {Attempt}/{Max} for {Function}",
                    attempt, MaxRetries, functionName);

                await next(context);

                // If we get here, the call succeeded
                _logger.LogInformation("[Recovery] {Function} succeeded on attempt {Attempt}", functionName, attempt);
                return;
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                // the caller asked to stop; retrying (and then falling back to a different
                // function) is not recovery
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[ErrorHandling] {Function} failed (attempt {Attempt}): {Error}",
                    functionName, attempt, ex.Message);

                if (attempt < MaxRetries)
                {
                    // Exponential backoff with jitter
                    var delay = TimeSpan.FromMilliseconds(
                        Math.Pow(2, attempt) * 500 + Random.Shared.Next(0, 200));

                    _logger.LogInformation("[Retry] Waiting {Delay}ms before retry...", delay.TotalMilliseconds);
                    await Task.Delay(delay, context.CancellationToken);
                }
            }

        // All retries exhausted → fall back
        _logger.LogWarning(
            "[Fallback] {Function} failed after {Max} attempts. Switching to GetGeneralAreaInfo.",
            functionName, MaxRetries);

        // Extract a city name from the original arguments (simplified)
        var originalAddress = context.Arguments["address"]?.ToString() ?? "unknown";
        var parts = originalAddress.Split(',');
        var city = (parts.Length >= 2 ? parts[^2] : parts[^1]).Trim(); // naive extraction: "street, city, country"

        // Override the result with the fallback output
        var fallbackResult = await context.Kernel
            .Plugins["LocationPlugin"]["GetGeneralAreaInfo"]
            .InvokeAsync<string>(context.Kernel, new KernelArguments { ["city"] = city });

        context.Result = new FunctionResult(context.Function, fallbackResult);

        _logger.LogInformation(
            "[Recovery] Fallback succeeded. Returned general area info for '{City}'.", city);
    }
}