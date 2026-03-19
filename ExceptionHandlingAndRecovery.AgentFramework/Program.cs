using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

const int maxRetries = 3;

async Task<AgentResponse> RetryAndFallbackMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    Exception? lastError = null;

    for (var attempt = 1; attempt <= maxRetries; attempt++)
        try
        {
            Console.WriteLine($"[RunMiddleware] Attempt {attempt}/{maxRetries}");

            var response = await innerAgent.RunAsync(
                messages, session, options, cancellationToken);

            // Simplistic check for the error, it's better done with structured tool responses or another call to an LLM, but this is just an example
            var responseText = string.Join(" ",
                response.Messages.Select(m => m.Text));
            var isError = responseText.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                          responseText.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                          responseText.Contains("unable", StringComparison.OrdinalIgnoreCase);
            if (isError &&
                attempt < maxRetries)
            {
                Console.WriteLine("  [RunMiddleware] Agent reported a tool error. Retrying...");
                var delay = (int)(Math.Pow(2, attempt) * 500 + Random.Shared.Next(0, 200));
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            Console.WriteLine($"  [RunMiddleware] Success on attempt {attempt}.");
            return response;
        }
        catch (Exception ex)
        {
            lastError = ex;
            Console.WriteLine($"  [RunMiddleware] Failed: {ex.Message}");

            if (attempt < maxRetries)
            {
                var delay = (int)(Math.Pow(2, attempt) * 500 + Random.Shared.Next(0, 200));
                Console.WriteLine($"  [Retry] Backing off {delay}ms...");
                await Task.Delay(delay, cancellationToken);
            }
        }

    // All retries exhausted -> return a graceful degradation response
    // The middleware returns an AgentResponse directly, skipping the agent
    Console.WriteLine("  [RunMiddleware] All retries exhausted. Returning fallback response.");
    return new AgentResponse([
        new ChatMessage(ChatRole.Assistant,
            $"I'm sorry, I wasn't able to get the precise location due to a service outage. " +
            $"Please try again in a few minutes, or I can provide general area information instead. " +
            $"(Error: {lastError?.Message})")
    ]);
}

var chatClient = Settings.ChatClient;
var agent = new ChatClientAgent(chatClient,
        """
        You are a helpful location assistant.
        Your primary tool is GetPreciseLocation. Use it first.
        If it fails, fall back to GetGeneralAreaInfo with just the city name.
        If the result has low confidence, tell the user the answer is approximate.
        Always present the location data clearly and concisely.
        """,
        "ResilientLocationAgent",
        tools:
        [
            LocationTools.PreciseLookup,
            LocationTools.GeneralLookup
        ])
    .AsBuilder()
    .Use(RetryAndFallbackMiddleware, null)
    .Build();

var session = await agent.CreateSessionAsync();

Console.WriteLine("User: Find the precise location of '15 Rue de Rivoli, Paris, France'.\n");

var result = await agent.RunAsync(
    "Find the precise location of '15 Rue de Rivoli, Paris, France'.",
    session);

Console.WriteLine($"\nAgent response:\n{result}");