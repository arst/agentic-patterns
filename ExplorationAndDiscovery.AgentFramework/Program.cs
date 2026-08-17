using Microsoft.Agents.AI;
using Shared;

var chatClient = Settings.ChatClient;
AIAgent researcher = new ChatClientAgent(chatClient, name: "Researcher",
    instructions: """
                  You are a creative research scientist. GENERATE novel hypotheses about the given topic.
                  For each hypothesis:
                  1. State the hypothesis clearly.
                  2. Explain the supporting reasoning.
                  3. Describe how it could be tested.
                  4. Rate confidence (low/medium/high).

                  Generate 3 distinct hypotheses varying in approach and boldness.
                  Be creative — explore unconventional angles.

                  Format each as:
                  ## Hypothesis N: [title]
                  **Claim**: ...
                  **Reasoning**: ...
                  **Testability**: ...
                  **Confidence**: ...
                  """);

AIAgent critic = new ChatClientAgent(chatClient, name: "Critic",
    instructions: """
                  You are a rigorous scientific reviewer. CRITICALLY EVALUATE hypotheses:
                  1. NOVELTY: Genuinely new or restating existing knowledge?
                  2. FEASIBILITY: Realistically testable with current methods?
                  3. EVIDENCE: Well-supported by existing literature?
                  4. WEAKNESSES: Gaps, assumptions, potential flaws?
                  5. Score 1-10 on overall promise.

                  Be constructive but rigorous. Provide your full evaluation as feedback,
                  identify the SINGLE most promising hypothesis, and rate the overall
                  quality of the set as low, medium or high.
                  """);

AIAgent evolver = new ChatClientAgent(chatClient, name: "Evolver",
    instructions: """
                  You are a scientific strategist. EVOLVE and REFINE hypotheses based on critique.
                  1. Take the most promising hypothesis.
                  2. Address each identified weakness.
                  3. Strengthen reasoning and evidence.
                  4. Explore one UNEXPECTED ANGLE from a different field.
                  5. Produce a refined, stronger version.

                  Also produce ONE entirely new hypothesis inspired by the gaps found.

                  Format:
                  ## Refined Hypothesis: [title]
                  **Original weakness addressed**: ...
                  **Improved claim**: ...
                  **Cross-disciplinary insight**: ...
                  **Confidence**: ...

                  ## New Hypothesis: [title]
                  **Inspired by gap**: ...
                  **Claim**: ...
                  """);

const int maxIterations = 2;

var topic = "What are novel approaches to reducing antibiotic resistance " +
            "in hospital settings that don't involve developing new antibiotics?";
var currentHypotheses = "";
var lastCritique = "";

Console.WriteLine($"Research Topic: {topic}\n");

for (var iteration = 1; iteration <= maxIterations; iteration++)
{
    Console.WriteLine($"\n--- Iteration {iteration}/{maxIterations} ---\n");
    Console.WriteLine("[Researcher] Generating hypotheses...\n");

    var genPrompt = iteration == 1
        ? $"Research topic: {topic}\n\nGenerate 3 novel hypotheses."
        : $"Research topic: {topic}\n\nPrevious evolved hypotheses:\n{currentHypotheses}\n\n" +
          "Build on these. Generate 3 NEW hypotheses in DIFFERENT directions.";

    var researcherResult = await researcher.RunAsync(genPrompt);
    var researcherOutput = researcherResult.Text;
    Console.WriteLine(researcherOutput);

    Console.WriteLine("\n\n[Critic] Evaluating hypotheses...\n");

    var criticResult = await critic.RunAsync<Critique>(
        $"Research topic: {topic}\n\nHypotheses to evaluate:\n{researcherOutput}");
    var critique = criticResult.Result;
    Console.WriteLine(critique.Feedback);
    Console.WriteLine($"MOST PROMISING: {critique.MostPromising}");
    Console.WriteLine($"OVERALL QUALITY: {critique.Quality}");
    lastCritique = critique.Feedback;

    if (critique.Quality.Equals("high", StringComparison.OrdinalIgnoreCase)
        && iteration > 1)
    {
        Console.WriteLine("\nHigh quality reached — stopping exploration.");
        break;
    }

    Console.WriteLine("\n\n[Evolver] Refining hypotheses...\n");

    var evolverResult = await evolver.RunAsync(
        $"Research topic: {topic}\n\n" +
        $"Original hypotheses:\n{researcherOutput}\n\n" +
        $"Critic's evaluation:\n{critique.Feedback}\n" +
        $"Most promising hypothesis: {critique.MostPromising}\n\n" +
        "Evolve the most promising hypothesis and generate one new one.");
    currentHypotheses = evolverResult.Text;
    Console.WriteLine(currentHypotheses);
}

Console.WriteLine("\n--- Final Discovery Summary ---\n");
Console.WriteLine($"## Exploration Complete\n\n" +
                  $"### Final Hypotheses\n{currentHypotheses}\n\n" +
                  $"### Last Critique\n{lastCritique}\n\n" +
                  "These are AI-generated hypotheses requiring human validation.");

internal sealed record Critique(string Feedback, string MostPromising, string Quality);
