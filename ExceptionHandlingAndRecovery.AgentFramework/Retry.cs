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
    /// <see cref="OperationCanceledException"/> is always rethrown, never retried. RunAsync takes no
    /// <see cref="CancellationToken"/> of its own, so it has nothing to test the exception against
    /// (the caller's token lives inside <paramref name="attempt"/>) — there is no way to tell "the
    /// caller cancelled" apart from "the tool timed out internally" here. One consequence: a tool that
    /// raises <see cref="OperationCanceledException"/> for its own internal timeout will no longer be
    /// retried by this helper either.
    /// </remarks>
    public static async Task<(AgentResponse? Response, Exception? LastError)> RunAsync(
        Func<int, Task<AgentResponse>> attempt, int maxRetries, Func<int, Task> backoff)
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
            catch (OperationCanceledException)
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
