using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shared;

var resource = ResourceBuilder.CreateDefault().AddService("AgentEvaluation");

var agentActivitySource = new ActivitySource("AgentEvaluation");
var agentMeter = new Meter("AgentEvaluation", "1.0.0");

var llmCallLatency = agentMeter.CreateHistogram<double>(
    "agent.llm.latency_ms", "ms", "Latency per LLM call");
var llmTokensUsed = agentMeter.CreateCounter<long>(
    "agent.llm.tokens", "{tokens}", "Total tokens consumed");
var agentCallCount = agentMeter.CreateCounter<long>(
    "agent.calls.count", "{calls}", "Total agent RunAsync calls");
var agentE2eLatency = agentMeter.CreateHistogram<double>(
    "agent.e2e.latency_ms", "ms", "End-to-end latency per agent call");

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

// IChatClient Middleware � Per-call telemetry
// Intercepts every LLM call to record:
//   - Latency per call
//   - Token usage from ChatResponse.Usage
//   - Model identifier

var llmCallCounter = 0;

async Task<ChatResponse> TelemetryMiddleware(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    IChatClient client,
    CancellationToken cancellationToken)
{
    var sw = Stopwatch.StartNew();
    Interlocked.Increment(ref llmCallCounter);

    // Start an OTel span for this LLM call
    using var activity = agentActivitySource.StartActivity("llm.chat.completion", ActivityKind.Client);
    activity?.SetTag("gen_ai.system", "openai");
    activity?.SetTag("gen_ai.request.model", Settings.AzureOpenAi.ChatModelDeployment);

    var response = await client.GetResponseAsync(messages, options, cancellationToken);

    sw.Stop();

    var inputTokens = response.Usage?.InputTokenCount ?? 0;
    var outputTokens = response.Usage?.OutputTokenCount ?? 0;
    var modelId = response.ModelId ?? Settings.AzureOpenAi.ChatModelDeployment;

    // Record into our custom telemetry collector
    telemetry.RecordCall(modelId, sw.Elapsed.TotalMilliseconds, inputTokens, outputTokens);

    // Emit OTel metrics
    llmCallLatency.Record(sw.Elapsed.TotalMilliseconds,
        new KeyValuePair<string, object?>("model", modelId));
    llmTokensUsed.Add(inputTokens + outputTokens,
        new KeyValuePair<string, object?>("model", modelId));

    // Enrich the OTel span with response metadata
    activity?.SetTag("gen_ai.response.model", modelId);
    activity?.SetTag("gen_ai.response.prompt_tokens", inputTokens);
    activity?.SetTag("gen_ai.response.completion_tokens", outputTokens);
    activity?.SetTag("gen_ai.response.finish_reason",
        response.FinishReason?.ToString() ?? "unknown");

    Console.WriteLine(
        $"  [Telemetry] {modelId}: {sw.Elapsed.TotalMilliseconds:F0}ms, " +
        $"{inputTokens}+{outputTokens} tokens");

    return response;
}

// Agent Run Middleware � End-to-end metrics + trajectory
// Wraps the entire agent execution to measure:
//   - Total end-to-end latency (including tool calls, retries)
//   - Number of LLM calls per user request
//   - Full trajectory record (user query ? agent response)

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

    // Start an OTel span for the full agent execution
    using var activity = agentActivitySource.StartActivity("agent.run");
    activity?.SetTag("agent.name", "SupportAgent");
    activity?.SetTag("agent.query_length", userQuery.Length);
    agentCallCount.Add(1);

    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

    sw.Stop();
    var callsForThisRequest = llmCallCounter - callsBefore;
    var responseText = string.Join("", response.Messages.Select(m => m.Text ?? ""));

    // Record trajectory
    telemetry.RecordTrajectory(userQuery, responseText, sw.Elapsed.TotalMilliseconds, callsForThisRequest);

    // Emit OTel metrics
    agentE2eLatency.Record(sw.Elapsed.TotalMilliseconds);

    // Enrich the OTel span
    activity?.SetTag("agent.llm_calls", callsForThisRequest);
    activity?.SetTag("agent.response_length", responseText.Length);

    Console.WriteLine(
        $"  [Trajectory] Total: {sw.Elapsed.TotalMilliseconds:F0}ms, " +
        $"{callsForThisRequest} LLM call(s)");

    return response;
}

var client = Settings
    .ChatClient
    .AsBuilder()
    .Use(TelemetryMiddleware, null)
    .Build();

var agent = new ChatClientAgent(Settings.ChatClient,
        name: "SupportAgent",
        instructions: """
                      You are a helpful customer support agent for TechCorp.
                      Answer questions about products and services concisely.
                      If a question is outside your scope, politely decline.
                      """
    )
    .AsBuilder()
    .Use(TrajectoryMiddleware, null)
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