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
