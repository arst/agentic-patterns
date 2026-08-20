using Microsoft.Agents.AI;
using SelfCorrectionLoop.AgentFramework;
using Shared;

var generator = new ChatClientAgent(Settings.ChatClient,
    "Write or revise the requested social post. Return only the post.", "ContentGenerator");
var evaluator = new ChatClientAgent(Settings.ChatClient,
    """
    Evaluate only subjective criteria: clarity, engagement, tone, and persuasiveness.
    Return a typed evaluation with one CriterionResult per criterion and concrete revision feedback.
    Do not judge character count, required names, forbidden terms, or other deterministic rules.
    """, "QualityEvaluator");

const string productName = "GreenTech Gadgets";
const int characterLimit = 150;
const int maximumIterations = 3;
var requirements = $"Write an engaging social post announcing {productName}, an eco-friendly product line, " +
                   $"with a clear call to action. Maximum {characterLimit} characters.";
var feedback = "";
var draft = "";
var candidates = new List<(string Draft, double Score)>();

for (var iteration = 1; iteration <= maximumIterations; iteration++)
{
    var prompt = iteration == 1
        ? requirements
        : $"{requirements}\n\nPrevious draft:\n{draft}\n\nEvaluation feedback:\n{feedback}\n\nRevise it.";
    draft = (await generator.RunAsync(prompt)).Text.Trim();
    var judged = (await evaluator.RunAsync<Evaluation>(
        $"Requirements:\n{requirements}\n\nDraft:\n{draft}")).Result;
    var evaluation = HostEvaluation.Apply(draft, judged, characterLimit, productName, ["guaranteed"]);
    candidates.Add((draft, evaluation.Score));

    Console.WriteLine($"--- Iteration {iteration}/{maximumIterations} ---");
    Console.WriteLine($"Draft: {draft}");
    foreach (var criterion in evaluation.Criteria)
        Console.WriteLine($"  [{(criterion.Passed ? "pass" : "fail")}] {criterion.Name}: {criterion.Feedback}");

    if (evaluation.Approved)
    {
        Console.WriteLine($"\nApproved after {iteration} iteration(s).\nFinal post: {draft}");
        return;
    }

    feedback = evaluation.Feedback;
}

var best = candidates.Where(c => c.Draft.Length <= characterLimit &&
                                  c.Draft.Contains(productName, StringComparison.OrdinalIgnoreCase) &&
                                  !c.Draft.Contains("guaranteed", StringComparison.OrdinalIgnoreCase))
    .DefaultIfEmpty(candidates.MaxBy(c => c.Score))
    .MaxBy(c => c.Score);
Console.WriteLine($"\nMaximum iterations reached; best candidate remains unapproved.\nFinal post: {best.Draft}");
