using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;
using SpeculativeToolExecution.AgentFramework;

// Speculative tool execution: start the calls the model is *probably* about to make, while it is
// still deciding, and serve them from the results already in flight.
//
// This is not parallelisation. Parallelisation runs calls the model has already committed to.
// Speculation runs calls it has not asked for yet and may never ask for - so it trades money for
// latency, and only two kinds of tool may be speculated: read-only, and free to throw away.
// The host enforces that; the model is never consulted about it.

var client = Settings.ChatClient;

// The policy table. `book_meeting` is read-write and `premium_market_data` is metered - both are
// excluded, and the exclusion is structural rather than a note in the prompt.
var policy = new Dictionary<string, SpeculatableTool>(StringComparer.OrdinalIgnoreCase)
{
    ["get_weather"] = new("get_weather", ReadOnly: true, FreeToDiscard: true),
    ["get_calendar"] = new("get_calendar", ReadOnly: true, FreeToDiscard: true),
    ["get_traffic"] = new("get_traffic", ReadOnly: true, FreeToDiscard: true),
    ["premium_market_data"] = new("premium_market_data", ReadOnly: true, FreeToDiscard: false),
    ["book_meeting"] = new("book_meeting", ReadOnly: false, FreeToDiscard: false)
};

var speculator = new Speculator(policy);

// Slow, mock backends - 600ms is what makes speculation worth anything.
static async Task<string> Slow(string result)
{
    await Task.Delay(600);
    return result;
}

Task<string> Weather(string city) => Slow($"{city}: 4°C, rain from 15:00");
Task<string> Calendar(string day) => Slow($"{day}: 09:00 standup, 11:00 client call, 16:00 free");
Task<string> Traffic(string city) => Slow($"{city}: A100 congested until 18:00");

// ── Speculate on the obvious three, before the model has said anything ───────
var began = Stopwatch.GetTimestamp();
Console.WriteLine("Speculating on the likely reads before the first token:");
foreach (var (tool, key, call) in new (string, string, Func<Task<string>>)[]
         {
             ("get_weather", "weather:Berlin", () => Weather("Berlin")),
             ("get_calendar", "calendar:tomorrow", () => Calendar("Tomorrow")),
             ("get_traffic", "traffic:Berlin", () => Traffic("Berlin")),
             ("premium_market_data", "market:DAX", () => Slow("DAX 18,402")),
             ("book_meeting", "book:16:00", () => Slow("booked"))
         })
    Console.WriteLine($"  {(speculator.Speculate(tool, key, call) ? "started " : "refused ")} {tool}" +
                      $" ({(policy[tool].CanSpeculate ? "speculatable" : "not speculatable by policy")})");

// ── The agent runs; its tool calls resolve against the speculations ──────────
var agent = new ChatClientAgent(client, name: "Assistant",
    instructions: "You are a scheduling assistant. Use the tools you need, then answer in two " +
                  "or three sentences.",
    tools:
    [
        AIFunctionFactory.Create(
            (string city) => speculator.ResolveAsync($"weather:{city}", () => Weather(city)),
            "get_weather", "Weather for a city."),
        AIFunctionFactory.Create(
            (string day) => speculator.ResolveAsync($"calendar:{day}", () => Calendar(day)),
            "get_calendar", "Calendar for a day, e.g. 'tomorrow'."),
        AIFunctionFactory.Create(
            (string city) => speculator.ResolveAsync($"traffic:{city}", () => Traffic(city)),
            "get_traffic", "Traffic for a city.")
    ]);

var answer = await agent.RunAsync(
    "I'm in Berlin. Should I cycle to my client call tomorrow, and when am I free afterwards?",
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.1f }));

Console.WriteLine($"\n=== Answer ({Stopwatch.GetElapsedTime(began).TotalSeconds:F1}s total) ===\n{answer}");

// ── The number that decides whether this pattern is worth it ─────────────────
var hits = speculator.Outcomes.Count(o => o.Hit);
var wasted = await speculator.DrainAsync();

Console.WriteLine($"\n=== Speculation ===");
foreach (var outcome in speculator.Outcomes)
    Console.WriteLine(outcome.Hit
        ? $"  hit  {outcome.Key} (already {outcome.Saved.TotalMilliseconds:F0}ms in flight when asked for)"
        : $"  miss {outcome.Key} (not speculated; ran on demand)");

Console.WriteLine($"\n{hits}/{speculator.Outcomes.Count} tool calls served from speculation; " +
                  $"{wasted} speculation(s) discarded unused.");
Console.WriteLine("A miss costs a wasted call, a hit saves a round trip. Below roughly a 50% hit " +
                  "rate on a slow tool, don't.");
