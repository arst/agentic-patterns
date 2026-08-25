using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Planning.SemanticKernel;
using Shared;

var kernel = Settings.Kernel;

var tools = kernel.ImportPluginFromType<TravelTools>();

// Never goes stale: the sample always asks for a date a month out.
var travelDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
var request = $"Book me the cheapest flight from BER to AMS on {travelDate:yyyy-MM-dd} " +
              "and draft a confirmation email.";

// Rendered via {{$placeholderExample}} rather than written literally in the prompt below - a
// literal "{{step2}}" in the template text would be parsed by Semantic Kernel's own templating
// engine instead of reaching the model as an example.
const string placeholderExample = "{{step2}}";

var result = await kernel.InvokePromptAsync(
    """"
    You are a planning agent. Produce a minimal ordered plan using ONLY these tools:
    - GetFlights(from,to,date)
    - SelectCheapest(flights)
    - RequestBookingApproval(flight)
    - BookFlight(approvedFlight)
    - DraftEmail(confirmation)

    SelectCheapest is the only way to choose a flight - it is the sole source of truth for
    "cheapest"; never pick a flight id yourself from GetFlights output.

    Rules:
    - Use as few steps as possible (max 5).
    - Ensure later steps only depend on outputs from earlier steps.
    - To pass an earlier step's output as an arg value, use the placeholder {{$placeholderExample}}, e.g. "confirmation": {{$placeholderExample}}.
    - Do not invent tool names.

    User request:
    {{$request}}
    """",
    new KernelArguments(new OpenAIPromptExecutionSettings
    {
        ResponseFormat = typeof(Plan)
    })
    {
        ["request"] = request,
        ["placeholderExample"] = placeholderExample
    });

var plan = JsonSerializer.Deserialize<Plan>(result.ToString())!;

Console.WriteLine("=== Plan ===");
foreach (var s in plan.Steps)
    Console.WriteLine($"{s.Id}. {s.Tool} - {s.Description}");

// A model-generated plan is untrusted input: validate the whole thing before any tool runs.
var errors = PlanValidator.Validate(plan, tools.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
    maxSteps: 5);
if (errors.Count > 0)
{
    Console.WriteLine("Plan rejected before any tool ran:");
    foreach (var error in errors) Console.WriteLine($"  step {error.StepId}: {error.Message}");
    return;
}

Console.WriteLine("\n=== Execution ===");

var memory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

foreach (var step in plan.Steps)
{
    try
    {
        var resolvedArgs = PlanValidator.Resolve(step.Args, memory);
        var stepArgs = new KernelArguments();
        foreach (var (key, value) in resolvedArgs)
            stepArgs[key] = value;

        var output = await kernel.InvokeAsync(tools.Name, step.Tool, stepArgs);

        memory[step.Id.ToString()] = output.ToString();
        Console.WriteLine($"\n[Step {step.Id}] {step.Tool} output:\n{output}");
    }
    catch (Exception ex)
    {
        // A denied approval, an unresolved placeholder, or any other step failure (e.g. a
        // mis-shaped argument) stops the plan here, not the process.
        Console.WriteLine($"\nPlan stopped at step {step.Id} ({step.Tool}): {ex.Message}");
        return;
    }
}
