using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using PromptChaining.SemanticKernel;
using Shared;

var kernel = Kernel.CreateBuilder()
    .AddAzureOpenAIChatCompletion(
        Settings.AzureOpenAi.ChatModelDeployment,
        Settings.AzureOpenAi.Endpoint,
        Settings.AzureOpenAi.ApiKey)
    .Build();

var input = """
            Contoso is considering acquiring Fabrikam. Alice (CFO) said the top priorities are:
            reducing cloud spend and accelerating time-to-market. The decision is expected in Q2.
            """;

// Step 1: Extract structured entities from unstructured text
var entities = await ExtractEntities(kernel, input);

// Step 2: Summary uses structured output from Step 1
var summary = await GenerateSummary(kernel, input, entities);

// Step 3: Draft email from summary
var email = await GenerateEmail(kernel, summary);

Console.WriteLine("=== Entities ===");
Console.WriteLine(JsonSerializer.Serialize(entities));
Console.WriteLine("\n=== Summary ===");
Console.WriteLine(summary);
Console.WriteLine("\n=== Email ===");
Console.WriteLine(email);


async Task<ExtractedEntities> ExtractEntities(Kernel kernel1, string s)
{
    var extractPrompt = """
                        You are an information extraction engine.
                        Extract people, organizations, and topics from the text.
                        Output ONLY valid JSON matching this schema:
                        {
                          "people": ["..."],
                          "orgs": ["..."],
                          "topics": ["..."]
                        }

                        TEXT:
                        {{$text}}
                        """;
    var entitiesJson1 = await InvokePromptAsync(
        kernel1,
        extractPrompt,
        new KernelArguments(new OpenAIPromptExecutionSettings
        {
            ResponseFormat = typeof(ExtractedEntities)
        }) { ["text"] = s });

    ExtractedEntities extractedEntities;
    try
    {
        extractedEntities = JsonSerializer.Deserialize<ExtractedEntities>(entitiesJson1,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? throw new JsonException("Null JSON result.");
    }
    catch (Exception ex)
    {
        // Guardrail: step-local failure handling (retry step 1, tighten prompt, log, etc.)
        throw new InvalidOperationException("Step 1 failed: invalid JSON entities output.", ex);
    }

    return extractedEntities;
}

async Task<string> GenerateSummary(Kernel kernel2, string input1, ExtractedEntities entities1)
{
    var summarizePrompt = """
                          Summarize the text in 5 bullet points.
                          Ensure you explicitly mention:
                          - People: {{$people}}
                          - Organizations: {{$orgs}}
                          - Topics: {{$topics}}

                          TEXT:
                          {{$text}}
                          """;

    var summaryResponse = await InvokePromptAsync(
        kernel2, summarizePrompt,
        new KernelArguments
        {
            ["text"] = input1,
            ["people"] = string.Join(", ", entities1.People),
            ["orgs"] = string.Join(", ", entities1.Orgs),
            ["topics"] = string.Join(", ", entities1.Topics)
        });
    return summaryResponse;
}

async Task<string> GenerateEmail(Kernel kernel3, string summary1)
{
    {
        var emailPrompt = """
                          Write a concise internal email (<= 150 words) to leadership.
                          Use the following summary as source of truth.

                          SUMMARY:
                          {{$summary}}
                          """;

        return await InvokePromptAsync(kernel3, emailPrompt, new KernelArguments { ["summary"] = summary1 });
    }
}

static async Task<string> InvokePromptAsync(
    Kernel kernel,
    string promptTemplate,
    KernelArguments args)
{
    var result = await kernel.InvokePromptAsync(promptTemplate, args);

    return result.ToString().Trim();
}