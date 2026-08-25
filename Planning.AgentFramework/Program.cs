using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Planning.AgentFramework;
using Shared;

var chatClient = Settings.ChatClient;

var planner = new ChatClientAgent(
    chatClient,
    name: "planner",
    instructions: """
                  You are a planning agent. Produce a minimal ordered plan using ONLY these tools:
                  - GetFlights(from,to,date)
                  - SelectCheapest(flights)
                  - RequestBookingApproval(flight)
                  - BookFlight(approvedFlight)
                  - DraftEmail(confirmation)

                  SelectCheapest is the only way to choose a flight - it is the sole source of truth
                  for "cheapest"; never pick a flight id yourself from GetFlights output.

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

// Never goes stale: the sample always asks for a date a month out.
var travelDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
var goal = $"Book me the cheapest flight from BER to AMS on {travelDate:yyyy-MM-dd} " +
           "and draft a confirmation email.";

var planResp = await planner.RunAsync<Plan>(goal, session);
var plan = planResp.Result;

Console.WriteLine("=== Plan ===");
foreach (var s in plan.Steps)
    Console.WriteLine($"{s.Id}. {s.Tool} - {s.Description}");

// Idempotency key minted once per run so a retried BookFlight call replays instead of booking twice.
var bookingKey = Guid.NewGuid().ToString("N");

// ponytail: in-memory dictionary standing in for a real idempotent booking service; swap for
// durable keyed storage (see IdempotentToolCalls) if this needs to survive a process restart.
var bookings = new Dictionary<string, string>(StringComparer.Ordinal);

var tools = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase)
{
    ["GetFlights"] = AIFunctionFactory.Create(GetFlights, "GetFlights"),
    ["SelectCheapest"] = AIFunctionFactory.Create(SelectCheapest, "SelectCheapest"),
    ["RequestBookingApproval"] = AIFunctionFactory.Create(RequestBookingApproval, "RequestBookingApproval"),
    ["BookFlight"] = AIFunctionFactory.Create(BookFlight, "BookFlight"),
    ["DraftEmail"] = AIFunctionFactory.Create(DraftEmail, "DraftEmail")
};

// A model-generated plan is untrusted input: validate the whole thing before any tool runs.
var errors = PlanValidator.Validate(plan, tools.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), maxSteps: 5);
if (errors.Count > 0)
{
    Console.WriteLine("Plan rejected before any tool ran:");
    foreach (var error in errors) Console.WriteLine($"  step {error.StepId}: {error.Message}");
    return;
}

var memory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

foreach (var step in plan.Steps)
{
    var tool = tools[step.Tool];

    try
    {
        var stepArgs = new AIFunctionArguments(
            PlanValidator.Resolve(step.Args, memory).ToDictionary(kv => kv.Key, object? (kv) => kv.Value));

        var output = (await tool.InvokeAsync(stepArgs))?.ToString() ?? "";

        memory[step.Id.ToString()] = output;
        Console.WriteLine($"\n[Step {step.Id}] {step.Tool} output:\n{output}");
    }
    catch (InvalidOperationException ex)
    {
        // A denied approval or an unresolved placeholder stops the plan here, not the process.
        Console.WriteLine($"\nPlan stopped at step {step.Id} ({step.Tool}): {ex.Message}");
        return;
    }
}

Console.WriteLine("\n=== Done ===");

return;

// Priced options - "cheapest" is now answerable from evidence.
static string GetFlights(string from, string to, string date) => JsonSerializer.Serialize(new[]
{
    new FlightOption("F100", "09:00", 189.00m),
    new FlightOption("F200", "13:30", 142.50m),
    new FlightOption("F300", "19:45", 167.25m)
});

// Deterministic HOST selection. The model does not get to pick "the cheapest" from free text.
static string SelectCheapest(string flights) =>
    JsonSerializer.Serialize(JsonSerializer.Deserialize<FlightOption[]>(flights)!
        .MinBy(f => f.PriceEur) ?? throw new InvalidOperationException("No flights to choose from."));

// Human-in-the-loop: the approval is bound to the exact flight and price, not to "a booking".
static string RequestBookingApproval(string flight)
{
    var option = JsonSerializer.Deserialize<FlightOption>(flight)!;
    Console.Write($"Approve booking {option.FlightId} at EUR {option.PriceEur:F2}? (yes/no): ");
    var answer = Console.ReadLine(); // EOF -> null -> denied, never auto-approved
    if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Booking was not approved; the plan stops here.");
    return flight;
}

// Idempotent: the key is minted by the host per plan run, so a retry books once.
string BookFlight(string approvedFlight)
{
    var option = JsonSerializer.Deserialize<FlightOption>(approvedFlight)!;
    if (bookings.TryGetValue(bookingKey, out var existing)) return existing; // replay, no second booking
    return bookings[bookingKey] =
        $"[Booked] {option.FlightId} at EUR {option.PriceEur:F2} (confirmation: ABC123)";
}

static string DraftEmail(string confirmation)
{
    return $"Subject: Flight booked\n\n{confirmation}\n\nThanks!";
}
