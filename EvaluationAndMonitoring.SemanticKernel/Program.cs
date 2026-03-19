using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shared;

var resource = ResourceBuilder.CreateDefault().AddService("AgentEvaluation");

// Enable sensitive data in telemetry (prompts/completions in traces)
AppContext.SetSwitch(
    "Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);

using var traceProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource("Microsoft.SemanticKernel*") // SK's built-in traces
    .AddSource("AgentEvaluation") // Our custom traces
    .AddConsoleExporter() // Dev: console; Prod: OTLP
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter("Microsoft.SemanticKernel*") // SK's built-in metrics
    .AddMeter("AgentEvaluation") // Our custom metrics
    .AddConsoleExporter()
    .Build();

// Custom Metrics via IFunctionInvocationFilter
// While SK provides built-in telemetry, you often need custom metrics:
//   - Per-tool-call latency histograms
//   - Business-specific counters (e.g., "escalations to human")
//   - Accumulated token costs

var evalMeter = new Meter("AgentEvaluation", "1.0.0");
var callLatency = evalMeter.CreateHistogram<double>(
    "agent.call.latency_ms", "ms", "Latency per agent call");
var totalTokens = evalMeter.CreateCounter<long>(
    "agent.tokens.total", "{tokens}", "Total tokens consumed");
var callCount = evalMeter.CreateCounter<long>(
    "agent.calls.count", "{calls}", "Total agent calls");

var evalActivitySource = new ActivitySource("AgentEvaluation");

var builder = Settings.CreateKernelBuilder();
builder.Services.AddSingleton<IFunctionInvocationFilter>(
    new MetricsFilter(callLatency, totalTokens, callCount, evalActivitySource));

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddOpenTelemetry(opts =>
    {
        opts.SetResourceBuilder(resource);
        opts.AddConsoleExporter();
    });
    b.SetMinimumLevel(LogLevel.Information);
});
builder.Services.AddSingleton(loggerFactory);

var kernel = builder.Build();

ChatCompletionAgent agent = new()
{
    Name = "SupportAgent",
    Instructions = """
                   You are a helpful customer support agent for TechCorp.
                   Answer questions about products and services concisely.
                   """,
    Kernel = kernel
};

Console.WriteLine("---- Running agent with telemetry ----\n");

var thread = new ChatHistoryAgentThread();
var testQueries = new[]
{
    "What warranty do TechCorp laptops come with?",
    "How do I return a defective product?",
    "What's the meaning of life?"
};

var responses = new List<(string Query, string Response, double LatencyMs)>();

foreach (var query in testQueries)
{
    Console.WriteLine($"?? User: {query}");
    var sw = Stopwatch.StartNew();

    var responseText = "";
    await foreach (var chunk in agent.InvokeAsync(query, thread))
        responseText += chunk.Message.Content;

    sw.Stop();
    responses.Add((query, responseText, sw.Elapsed.TotalMilliseconds));
    Console.WriteLine($" Agent: {responseText}");
    Console.WriteLine($"   {sw.Elapsed.TotalMilliseconds:F0}ms\n");
}