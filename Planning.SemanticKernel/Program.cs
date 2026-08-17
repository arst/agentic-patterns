using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Planning.SemanticKernel;
using Shared;

var kernel = Settings.Kernel;

var tools = kernel.ImportPluginFromType<TravelTools>();

var result = await kernel.InvokePromptAsync(
    """"
    You are a planning agent. Produce a minimal ordered plan using ONLY these tools:
    - GetFlights(from,to,date)
    - BookFlight(flightId)
    - DraftEmail(confirmation)

    Rules:
    - Use as few steps as possible (max 5).
    - Ensure later steps only depend on outputs from earlier steps.
    - Do not invent tool names.

    User request:
    {{$request}}
    """",
    new KernelArguments(new OpenAIPromptExecutionSettings
    {
        ResponseFormat = typeof(Plan)
    }) { ["request"] = "Book a flight from NYC to Paris on 2026-12-01 and draft a confirmation email." });

var plan = JsonSerializer.Deserialize<Plan>(result.ToString())!;

Console.WriteLine("=== Plan ===");
foreach (var s in plan.Steps)
    Console.WriteLine($"{s.Id}. {s.Tool} - {s.Description}");

Console.WriteLine("\n=== Execution ===");

foreach (var step in plan.Steps.Take(5))
{
    var stepArgs = new KernelArguments();
    foreach (var (key, value) in step.Args)
        stepArgs[key] = value;

    var output = await kernel.InvokeAsync(tools.Name, step.Tool, stepArgs);

    Console.WriteLine($"\n[Step {step.Id}] {step.Tool} output:\n{output}");
}
