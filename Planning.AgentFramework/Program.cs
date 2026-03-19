using Microsoft.Agents.AI;
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

var memory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

foreach (var step in plan.Steps)
{
    var output = step.Tool switch
    {
        "GetFlights" => GetFlights(
            step.Args["from"],
            step.Args["to"],
            step.Args["date"]),
        "BookFlight" => BookFlight(step.Args["flightId"]),
        "DraftEmail" => DraftEmail(step.Args["confirmation"]),
        _ => throw new InvalidOperationException($"Tool not allowed: {step.Tool}")
    };

    memory[$"step_{step.Id}"] = output;
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