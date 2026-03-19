using Microsoft.SemanticKernel.Agents;
using Shared;

ChatCompletionAgent generator = new()
{
    Name = "ContentGenerator",
    Instructions = """
                   You write social media posts. When given feedback, incorporate it
                   into your next revision. Always output ONLY the revised post, nothing else.
                   """,
    Kernel = new Settings().Kernel
};

ChatCompletionAgent evaluator = new()
{
    Name = "QualityEvaluator",
    Instructions = """
                   You evaluate social media posts against requirements. For each post:
                   1. Check: Does it meet the character limit?
                   2. Check: Is it engaging (strong verbs, emotional hook)?
                   3. Check: Does it include the required product name?
                   4. Check: Does it mention eco-friendliness?
                   5. Check: Does it have a clear call to action?

                   If ALL checks pass, respond with exactly: "APPROVED"
                   If any check fails, respond with specific feedback for improvement.
                   Never write the post yourself — only critique.
                   """,
    Kernel = new Settings().Kernel
};

var requirements =
    "Write a short social media post (max 150 characters) announcing " +
    "'GreenTech Gadgets', a new eco-friendly product line. " +
    "Make it engaging with a clear call to action.";

const int maxIterations = 3;
var currentDraft = "";

for (var i = 1; i <= maxIterations; i++)
{
    Console.WriteLine($"--- Iteration {i}/{maxIterations} ---");

    // Generate (or revise)
    var genPrompt = i == 1
        ? requirements
        : $"Requirements: {requirements}\n\nPrevious draft: {currentDraft}\n\nFeedback: {currentDraft}\nRevise the post to address the feedback.";

    var genResponse = "";
    await foreach (var chunk in generator.InvokeAsync(genPrompt))
        genResponse += chunk.Message;

    currentDraft = genResponse.Trim();
    Console.WriteLine($"  Generator: {currentDraft}");

    // Evaluate
    var evalPrompt = $"Requirements: {requirements}\n\nPost to evaluate: {currentDraft}";
    var evalResponse = "";
    await foreach (var chunk in evaluator.InvokeAsync(evalPrompt))
        evalResponse += chunk.Message;

    Console.WriteLine($"  Evaluator: {evalResponse.Trim()}");

    if (evalResponse.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"\nApproved after {i} iteration(s).");
        break;
    }

    if (i == maxIterations)
        Console.WriteLine("\nMax iterations reached. Using best draft.");
}

Console.WriteLine($"\nFinal post: {currentDraft}");