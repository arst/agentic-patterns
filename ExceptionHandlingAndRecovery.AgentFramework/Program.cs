using ExceptionHandlingAndRecovery.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

const int maxRetries = 3;

async Task<AgentResponse> RetryAndFallbackMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session, // deliberately unused: each attempt gets a fresh session (see below)
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    var (response, lastError) = await Retry.RunAsync(
        async attempt =>
        {
            Console.WriteLine($"[RunMiddleware] Attempt {attempt}/{maxRetries}");

            // Fresh session per attempt so failed turns don't pollute history and get replayed on retry
            var attemptSession = await innerAgent.CreateSessionAsync(cancellationToken: cancellationToken);
            return await innerAgent.RunAsync(messages, attemptSession, options, cancellationToken);
        },
        maxRetries,
        attempt =>
        {
            var delay = (int)(Math.Pow(2, attempt) * 500 + Random.Shared.Next(0, 200));
            Console.WriteLine($"  [Retry] Backing off {delay}ms...");
            return Task.Delay(delay, cancellationToken);
        });

    if (response is not null)
    {
        Console.WriteLine("  [RunMiddleware] Success.");
        return response;
    }

    // All retries exhausted (thrown OR a tool error still present on the final attempt) ->
    // graceful degradation. A persistent tool failure is never returned as success.
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

Console.WriteLine("User: Find the precise location of '15 Rue de Rivoli, Paris, France'.\n");

var result = await agent.RunAsync(
    "Find the precise location of '15 Rue de Rivoli, Paris, France'.");

Console.WriteLine($"\nAgent response:\n{result}");