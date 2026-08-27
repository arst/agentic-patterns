using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ExceptionHandlingAndRecovery.AgentFramework;

internal static class Retry
{
    // Detect tool failures from the actual function results instead of guessing from response text
    public static bool HasToolError(AgentResponse response) =>
        response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Any(c => c.Exception is not null ||
                      (c.Result?.ToString()?.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// Runs attempt() up to maxRetries times. Returns (null, lastError) when every attempt threw
    /// OR the final attempt still reported a tool error — a persistent tool failure is never a success.
    /// </summary>
    /// <remarks>
    /// Whole-turn retry replays every tool call the turn made. Only use it for turns whose tools are
    /// read-only or idempotent; a turn that issues a refund must retry at the tool boundary with an
    /// idempotency key instead (see IdempotentToolCalls).
    ///
    /// <b>Cancellation expresses caller intent; a timeout expresses dependency failure.</b> .NET
    /// spells both with the same exception family, so this helper takes the caller's
    /// <paramref name="cancellationToken"/> and asks it directly instead of inferring intent from
    /// the exception type: an <see cref="OperationCanceledException"/> raised while that token is
    /// signalled is the caller asking to stop, and is rethrown — retrying is not recovery. One
    /// raised while the token is NOT signalled came from somewhere inside the attempt (a
    /// dependency's own deadline) and is a transient failure like any other, so it is retried.
    /// A dependency that owns a deadline should still surface it as its own exception type — see
    /// <c>LocationTools.GetPreciseLocation</c>, which converts its blown deadline into a
    /// <see cref="TimeoutException"/> rather than letting an ambiguous OCE escape.
    /// </remarks>
    public static async Task<(AgentResponse? Response, Exception? LastError)> RunAsync(
        Func<int, Task<AgentResponse>> attempt, int maxRetries, Func<int, Task> backoff,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;

        for (var attemptNumber = 1; attemptNumber <= maxRetries; attemptNumber++)
            try
            {
                var response = await attempt(attemptNumber);
                if (!Retry.HasToolError(response))
                    return (response, null);

                lastError = new InvalidOperationException("Agent reported a tool error.");
                Console.WriteLine("  [RunMiddleware] Agent reported a tool error.");
                if (attemptNumber < maxRetries)
                    await backoff(attemptNumber);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the caller asked to stop; retrying is not recovery
            }
            catch (Exception ex)
            {
                lastError = ex;
                Console.WriteLine($"  [RunMiddleware] Failed: {ex.Message}");
                if (attemptNumber < maxRetries)
                    await backoff(attemptNumber);
            }

        return (null, lastError);
    }
}
