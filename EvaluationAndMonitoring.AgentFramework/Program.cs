using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shared;

var resource = ResourceBuilder.CreateDefault().AddService("AgentEvaluation");

// The built-in MEAI/Agent Framework OpenTelemetry instrumentation emits
// gen_ai.* spans and metrics on the source name we pass to UseOpenTelemetry.
using var traceProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource("AgentEvaluation")
    .AddConsoleExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter("AgentEvaluation")
    .AddConsoleExporter()
    .Build();

var telemetry = new AgentTelemetry();

// IChatClient Middleware — feeds the custom AgentTelemetry summary
// (latency, token usage, model per LLM call). gen_ai spans/metrics come
// from the built-in UseOpenTelemetry instrumentation below.

var llmCallCounter = 0;

async Task<ChatResponse> TelemetryMiddleware(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    IChatClient client,
    CancellationToken cancellationToken)
{
    var sw = Stopwatch.StartNew();
    Interlocked.Increment(ref llmCallCounter);

    var response = await client.GetResponseAsync(messages, options, cancellationToken);

    sw.Stop();

    var inputTokens = response.Usage?.InputTokenCount ?? 0;
    var outputTokens = response.Usage?.OutputTokenCount ?? 0;
    var modelId = response.ModelId ?? Settings.AzureOpenAi.ChatModelDeployment;

    // Record into our custom telemetry collector
    telemetry.RecordCall(modelId, sw.Elapsed.TotalMilliseconds, inputTokens, outputTokens);

    Console.WriteLine(
        $"  [Telemetry] {modelId}: {sw.Elapsed.TotalMilliseconds:F0}ms, " +
        $"{inputTokens}+{outputTokens} tokens");

    return response;
}

// Agent Run Middleware — feeds the custom trajectory record:
//   - Total end-to-end latency (including tool calls, retries)
//   - Number of LLM calls per user request
//   - Full trajectory record (user query -> agent response)

async Task<AgentResponse> TrajectoryMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    var userQuery = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
    var callsBefore = llmCallCounter;
    var sw = Stopwatch.StartNew();

    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

    sw.Stop();
    var callsForThisRequest = llmCallCounter - callsBefore;
    var responseText = string.Join("", response.Messages.Select(m => m.Text ?? ""));

    // Record trajectory
    telemetry.RecordTrajectory(userQuery, responseText, sw.Elapsed.TotalMilliseconds, callsForThisRequest);

    Console.WriteLine(
        $"  [Trajectory] Total: {sw.Elapsed.TotalMilliseconds:F0}ms, " +
        $"{callsForThisRequest} LLM call(s)");

    return response;
}

var client = Settings
    .ChatClient
    .AsBuilder()
    .Use(TelemetryMiddleware, null)
    .UseOpenTelemetry(sourceName: "AgentEvaluation") // built-in gen_ai spans + metrics
    .Build();

var agent = new ChatClientAgent(client,
        name: "SupportAgent",
        instructions: """
                      You are a helpful customer support agent for TechCorp.
                      Answer questions about products and services concisely.
                      If a question is outside your scope, politely decline.
                      """
    )
    .AsBuilder()
    .Use(TrajectoryMiddleware, null)
    .UseOpenTelemetry("AgentEvaluation") // built-in invoke_agent spans
    .Build();

Console.WriteLine("---- Running agent with telemetry ----\n");

var testQueries = new[]
{
    "What warranty do TechCorp laptops come with?",
    "How do I return a defective product?",
    "What's the meaning of life?" // Off-topic
};

var session = await agent.CreateSessionAsync();
var responses = new List<(string Query, string Response)>();

foreach (var query in testQueries)
{
    Console.WriteLine($"\n?? User: {query}");
    var result = await agent.RunAsync(query, session);
    var responseText = result.ToString() ?? "";
    responses.Add((query, responseText));
    Console.WriteLine($" Agent: {responseText}");
}

telemetry.PrintSummary();
