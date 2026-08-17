using System.Diagnostics;
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
    .AddConsoleExporter() // Dev: console; Prod: OTLP
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter("Microsoft.SemanticKernel*") // SK's built-in metrics
    .AddConsoleExporter()
    .Build();

var builder = Settings.CreateKernelBuilder();

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

foreach (var query in testQueries)
{
    Console.WriteLine($"?? User: {query}");
    var sw = Stopwatch.StartNew();

    var responseText = "";
    await foreach (var chunk in agent.InvokeAsync(query, thread))
        responseText += chunk.Message.Content;

    sw.Stop();
    Console.WriteLine($" Agent: {responseText}");
    Console.WriteLine($"   {sw.Elapsed.TotalMilliseconds:F0}ms\n");
}