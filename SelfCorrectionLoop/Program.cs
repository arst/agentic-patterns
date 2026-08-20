using Microsoft.SemanticKernel.Agents;
using SelfCorrectionLoop;
using Shared;

ChatCompletionAgent generator = new()
{
    Name = "ContentGenerator",
    Instructions = """
                   You write social media posts. When given feedback, incorporate it
                   into your next revision. Always output ONLY the revised post, nothing else.
                   """,
    Kernel = Settings.Kernel
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

                   If ALL checks pass, respond with "APPROVED" on the first line.
                   If any check fails, respond with "REVISE" on the first line,
                   followed by specific feedback for improvement.
                   On the next line always output "SCORE: <0.0-1.0>" rating overall quality.
                   Never write the post yourself — only critique.
                   """,
    Kernel = Settings.Kernel
};

var requirements =
    "Write a short social media post (max 150 characters) announcing " +
    "'GreenTech Gadgets', a new eco-friendly product line. " +
    "Make it engaging with a clear call to action.";

const int maxIterations = 3;
const int charLimit = 150;
var currentDraft = "";
var latestFeedback = "";
string? approvedDraft = null;
var drafts = new List<(string Draft, double Score)>();

for (var i = 1; i <= maxIterations; i++)
{
    Console.WriteLine($"--- Iteration {i}/{maxIterations} ---");

    // Generate (or revise)
    var genPrompt = i == 1
        ? requirements
        : $"Requirements: {requirements}\n\nPrevious draft: {currentDraft}\n\nFeedback: {latestFeedback}\nRevise the post to address the feedback.";

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

    latestFeedback = evalResponse.Trim();
    Console.WriteLine($"  Evaluator: {latestFeedback}");

    // Code owns the hard constraint — an over-limit draft is rejected even if the LLM approved it.
    if (currentDraft.Length > charLimit)
    {
        latestFeedback = $"REVISE\nSCORE: 0.0\nHost check: post is {currentDraft.Length} chars; limit is {charLimit}.";
        Console.WriteLine($"  Host: over the {charLimit}-char limit ({currentDraft.Length} chars) — forcing REVISE.");
    }

    drafts.Add((currentDraft, DraftSelection.ParseScore(latestFeedback)));

    var verdict = latestFeedback.Split('\n')[0].Trim();
    if (verdict.StartsWith("APPROVED", StringComparison.OrdinalIgnoreCase))
    {
        approvedDraft = currentDraft;
        Console.WriteLine($"\nApproved after {i} iteration(s).");
        break;
    }

    if (i == maxIterations)
        Console.WriteLine("\nMax iterations reached. Using best draft.");
}

// An approved draft always wins; best-by-score is only the fallback when nothing was approved.
var finalPost = approvedDraft ?? DraftSelection.Best(drafts, charLimit).Draft;
Console.WriteLine($"\nFinal post: {finalPost}");