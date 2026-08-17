using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

var chatClient = Settings.ChatClient;

var planner = new ChatClientAgent(
    chatClient,
    name: "planner",
    instructions: """
                  You are a planning agent. Produce a minimal ordered plan using ONLY these tools:
                  - GetFlights(from,to,date)
                  - BookFlight(flightId)
                  - DraftEmail(confirmation)

                  Output ONLY JSON matching:
                  { "steps": [ { "id": 1, "tool": "GetFlights", "args": { ... }, "description": "..." }, ... ] }

                  Rules:
                  - Use as few steps as possible (max 5).
                  - Ensure later steps only depend on outputs from earlier steps.
                  - To pass an earlier step's output as an arg value, use the placeholder {{stepN}}, e.g. "confirmation": "{{step2}}".
                  - Do not invent tool names.
                  """
);

var session = await planner.CreateSessionAsync();

var goal = "Book me the cheapest flight from BER to AMS on 2026-04-03 and draft a confirmation email.";

var planResp = await planner.RunAsync<Plan>(goal, session);
var plan = planResp.Result;

Console.WriteLine("=== Plan ===");
foreach (var s in plan.Steps)
    Console.WriteLine($"{s.Id}. {s.Tool} - {s.Description}");

var tools = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase)
{
    ["GetFlights"] = AIFunctionFactory.Create(GetFlights, "GetFlights"),
    ["BookFlight"] = AIFunctionFactory.Create(BookFlight, "BookFlight"),
    ["DraftEmail"] = AIFunctionFactory.Create(DraftEmail, "DraftEmail")
};

var memory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

foreach (var step in plan.Steps.Take(5)) // enforce the max-5-steps rule
{
    if (!tools.TryGetValue(step.Tool, out var tool))
        throw new InvalidOperationException($"Tool not allowed: {step.Tool}");

    // Substitute {{stepN}} placeholders with earlier step outputs from memory.
    var stepArgs = new AIFunctionArguments(step.Args.ToDictionary(
        kv => kv.Key,
        object? (kv) => Regex.Replace(kv.Value, @"\{\{step(\d+)\}\}",
            m => memory.GetValueOrDefault(m.Groups[1].Value, m.Value))));

    var output = (await tool.InvokeAsync(stepArgs))?.ToString() ?? "";

    memory[step.Id.ToString()] = output;
    Console.WriteLine($"\n[Step {step.Id}] {step.Tool} output:\n{output}");
}

Console.WriteLine("\n=== Done ===");

return;

static string GetFlights(string from, string to, string date)
{
    return $"[Flights] {from}->{to} on {date}: F100 09:00, F200 13:30";
}

static string BookFlight(string flightId)
{
    return $"[Booked] {flightId} (confirmation: ABC123)";
}

static string DraftEmail(string confirmation)
{
    return $"Subject: Flight booked\n\n{confirmation}\n\nThanks!";
}