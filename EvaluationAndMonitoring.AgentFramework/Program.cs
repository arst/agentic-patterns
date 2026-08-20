using System.Diagnostics;
using EvaluationAndMonitoring.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shared;

const string promptVersion = "support-agent-v1";
var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "live";
var tracePath = args.ElementAtOrDefault(1) ?? Path.Combine(AppContext.BaseDirectory, "run-trace.json");
RunTrace? recordedTrace = null;
ReplayChatClient? replayClient = null;
ToolTraceSession? toolTrace = null;

IChatClient sourceClient;
if (mode == "replay")
{
    var trace = await TraceStore.LoadAsync(tracePath);
    if (trace.PromptVersion != promptVersion)
        throw new InvalidOperationException(
            $"Trace prompt version '{trace.PromptVersion}' does not match '{promptVersion}'.");
    replayClient = new ReplayChatClient(trace);
    toolTrace = new ToolTraceSession(trace, replay: true);
    sourceClient = replayClient;
    Console.WriteLine($"---- Replaying {trace.ModelCalls.Count} model and {trace.ToolCalls.Count} tool calls from {tracePath} ----\n");
}
else
{
    sourceClient = Settings.ChatClient;
    if (mode is "record" or "record-redacted" or "record-hashes")
    {
        var privacy = mode switch
        {
            "record-redacted" => TracePrivacyMode.RedactedContent,
            "record-hashes" => TracePrivacyMode.HashesOnly,
            _ => TracePrivacyMode.FullContent
        };
        recordedTrace = new RunTrace(promptVersion, privacy);
        sourceClient = new RecordingChatClient(sourceClient, recordedTrace);
        toolTrace = new ToolTraceSession(recordedTrace, replay: false);
        Console.WriteLine($"---- Recording {privacy} trace to {tracePath} ----\n");
    }
    else if (mode != "live")
        throw new ArgumentException("Mode must be live, record, record-redacted, record-hashes, or replay.");
    else
        Console.WriteLine("---- Running agent with telemetry ----\n");
}

var resource = ResourceBuilder.CreateDefault().AddService("AgentEvaluation");
using var traceProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource).AddSource("AgentEvaluation").AddConsoleExporter().Build();
using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource).AddMeter("AgentEvaluation").AddConsoleExporter().Build();

var telemetry = new AgentTelemetry();
var llmCallCounter = 0;

async Task<ChatResponse> TelemetryMiddleware(IEnumerable<ChatMessage> messages, ChatOptions? options,
    IChatClient client, CancellationToken cancellationToken)
{
    var sw = Stopwatch.StartNew();
    Interlocked.Increment(ref llmCallCounter);
    var response = await client.GetResponseAsync(messages, options, cancellationToken);
    var inputTokens = response.Usage?.InputTokenCount ?? 0;
    var outputTokens = response.Usage?.OutputTokenCount ?? 0;
    var modelId = response.ModelId ?? "unknown";
    telemetry.RecordCall(modelId, sw.Elapsed.TotalMilliseconds, inputTokens, outputTokens);
    Console.WriteLine($"  [Telemetry] {modelId}: {sw.Elapsed.TotalMilliseconds:F0}ms, " +
                      $"{inputTokens}+{outputTokens} tokens");
    return response;
}

async Task<AgentResponse> TrajectoryMiddleware(IEnumerable<ChatMessage> messages, AgentSession? session,
    AgentRunOptions? options, AIAgent innerAgent, CancellationToken cancellationToken)
{
    var userQuery = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
    var callsBefore = llmCallCounter;
    var sw = Stopwatch.StartNew();
    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
    var responseText = string.Join("", response.Messages.Select(m => m.Text ?? ""));
    telemetry.RecordTrajectory(userQuery, responseText, sw.Elapsed.TotalMilliseconds,
        llmCallCounter - callsBefore);
    Console.WriteLine($"  [Trajectory] Total: {sw.Elapsed.TotalMilliseconds:F0}ms, " +
                      $"{llmCallCounter - callsBefore} LLM call(s)");
    return response;
}

var client = sourceClient.AsBuilder()
    .Use(TelemetryMiddleware, null)
    .UseOpenTelemetry(sourceName: "AgentEvaluation")
    .Build();
var policyFunction = AIFunctionFactory.Create((string topic) => topic.ToLowerInvariant() switch
{
    "warranty" => "TechCorp laptops include a two-year limited warranty. Contact warranty@techcorp.example.",
    "returns" => "Defective products may be returned within 30 days with the order number.",
    _ => "No policy found."
}, "GetSupportPolicy", "Get the authoritative TechCorp warranty or returns policy.");
AITool policyTool = toolTrace is null ? policyFunction : new RecordedAIFunction(policyFunction, toolTrace);
var agent = new ChatClientAgent(client,
        name: "SupportAgent",
        instructions: """
                      You are a helpful customer support agent for TechCorp.
                      Use GetSupportPolicy for warranty and return questions, then answer concisely.
                      If a question is outside your scope, politely decline.
                      """, tools: [policyTool])
    .AsBuilder()
    .Use(TrajectoryMiddleware, null)
    .UseOpenTelemetry("AgentEvaluation")
    .Build();

var testQueries = new[]
{
    "What warranty do TechCorp laptops come with?",
    "How do I return a defective product?",
    "What's the meaning of life?"
};

try
{
    var session = await agent.CreateSessionAsync();
    foreach (var query in testQueries)
    {
        Console.WriteLine($"\nUser: {query}");
        Console.WriteLine($"Agent: {await agent.RunAsync(query, session)}");
    }

    if (replayClient is not null && replayClient.RemainingCalls != 0)
        throw new InvalidOperationException($"Replay left {replayClient.RemainingCalls} unused model calls.");
    if (mode == "replay" && toolTrace?.RemainingCalls != 0)
        throw new InvalidOperationException($"Replay left {toolTrace?.RemainingCalls} unused tool calls.");
    if (recordedTrace is not null) recordedTrace.StopReason = "Completed";
    telemetry.PrintSummary();
}
catch (Exception ex)
{
    if (recordedTrace is not null) recordedTrace.StopReason = $"Failed:{ex.GetType().Name}";
    throw;
}
finally
{
    if (recordedTrace is not null)
        await TraceStore.SaveAsync(tracePath, recordedTrace);
}
