using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Planning.SemanticKernel;
using Shared;

var kernel = Settings.Kernel;

kernel.ImportPluginFromType<TravelTools>();

var result = await InvokePromptAsync(
    kernel,
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
    }) { ["request"] = "Book a flight from NYC to Paris on 2024-12-01 and draft a confirmation email." });
Console.WriteLine("=== Final Answer ===");
var plan = JsonSerializer.Deserialize<Plan>(result);
Console.WriteLine(plan);

Console.WriteLine("\n=== Trace (messages include tool calls/results) ===");

static async Task<string> InvokePromptAsync(
    Kernel kernel,
    string promptTemplate,
    KernelArguments args)
{
    var result = await kernel.InvokePromptAsync(promptTemplate, args);

    return result.ToString().Trim();
}