using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Agent Framework middleware: agent.AsBuilder().Use(...) wraps an AIAgent in a pipeline.
// Two interception layers are demonstrated below:
//   1. Agent-run middleware   - wraps the whole RunAsync (logging, latency, token usage).
//   2. Function middleware    - wraps each tool call inside the run loop (audit + guard).
// A third layer exists below the agent: MEAI chat-client middleware, e.g.
//   Settings.ChatClient.AsBuilder().Use(inner => new MyDelegatingChatClient(inner)).Build()
// which intercepts every raw model request/response before the agent loop even sees it.

var innerAgent = new ChatClientAgent(Settings.ChatClient,
    "You are a helpful assistant. Use tools when needed.",
    tools: new List<AITool>
    {
        AIFunctionFactory.Create(GetWeather, nameof(GetWeather)),
        AIFunctionFactory.Create(DeleteFile, nameof(DeleteFile))
    });

var agent = innerAgent.AsBuilder()
    // Layer 1: agent-run middleware - intercepts every RunAsync on the agent.
    .Use(runFunc: async (messages, session, options, inner, ct) =>
        {
            Console.WriteLine($"  [run] -> \"{messages.Last().Text}\"");
            var stopwatch = Stopwatch.StartNew();
            var response = await inner.RunAsync(messages, session, options, ct);
            Console.WriteLine(
                $"  [run] <- done in {stopwatch.ElapsedMilliseconds} ms, " +
                $"tokens in/out: {response.Usage?.InputTokenCount}/{response.Usage?.OutputTokenCount}");
            return response;
        },
        runStreamingFunc: null)
    // Layer 2: function-invocation middleware - intercepts each tool call.
    .Use(async (_, context, next, ct) =>
    {
        var args = string.Join(", ", context.Arguments.Select(a => $"{a.Key}={a.Value}"));
        Console.WriteLine($"  [func] {context.Function.Name}({args})");

        if (context.Function.Name == nameof(DeleteFile))
        {
            Console.WriteLine("  [func] BLOCKED by guard middleware - the tool never runs");
            return "Denied: destructive operations are blocked by policy.";
        }

        return await next(context, ct);
    })
    .Build();

Console.WriteLine("=== Agent Framework middleware: run + function interception ===");

Console.WriteLine("\n--- Prompt 1: benign tool call (logged and allowed) ---");
Console.WriteLine(await agent.RunAsync("What is the weather like in Amsterdam?"));

Console.WriteLine("\n--- Prompt 2: dangerous tool call (guard rewrites the result) ---");
Console.WriteLine(await agent.RunAsync("Please delete the file /tmp/report.txt"));
return;

static string GetWeather(string location)
{
    return $"Weather in {location}: cloudy, 15°C";
}

static string DeleteFile(string path)
{
    Console.WriteLine($"  [tool] DeleteFile actually ran for {path} - guard failed!");
    return $"Deleted {path}";
}
