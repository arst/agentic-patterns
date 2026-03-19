using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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

                  Be constructive but rigorous. Identify the SINGLE most promising hypothesis.
                  End with: "MOST PROMISING: Hypothesis N" and "OVERALL QUALITY: [low/medium/high]"
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

async Task<AgentResponse> ExplorationMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    var topic = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
    string currentHypotheses = "";
    string lastCritique = "";

    Console.WriteLine($"Research Topic: {topic}\n");

    for (int iteration = 1; iteration <= maxIterations; iteration++)
    {
        Console.WriteLine($"\n--- Iteration {iteration}/{maxIterations} ---\n");
        Console.WriteLine("[Researcher] Generating hypotheses...\n");

        var genPrompt = iteration == 1
            ? $"Research topic: {topic}\n\nGenerate 3 novel hypotheses."
            : $"Research topic: {topic}\n\nPrevious evolved hypotheses:\n{currentHypotheses}\n\n" +
              "Build on these. Generate 3 NEW hypotheses in DIFFERENT directions.";

        var researcherResult = await researcher.RunAsync(genPrompt);
        var researcherOutput = researcherResult.ToString() ?? "";
        Console.WriteLine(researcherOutput);
        
        Console.WriteLine("\n\n[Critic] Evaluating hypotheses...\n");

        var criticResult = await critic.RunAsync(
            $"Research topic: {topic}\n\nHypotheses to evaluate:\n{researcherOutput}");
        var criticOutput = criticResult.ToString() ?? "";
        Console.WriteLine(criticOutput);
        lastCritique = criticOutput;
        
        if (criticOutput.Contains("OVERALL QUALITY: high", StringComparison.OrdinalIgnoreCase)
            && iteration > 1)
        {
            Console.WriteLine("\nHigh quality reached — stopping exploration.");
            break;
        }
        
        Console.WriteLine("\n\n[Evolver] Refining hypotheses...\n");

        var evolverResult = await evolver.RunAsync(
            $"Research topic: {topic}\n\n" +
            $"Original hypotheses:\n{researcherOutput}\n\n" +
            $"Critic's evaluation:\n{criticOutput}\n\n" +
            "Evolve the most promising hypothesis and generate one new one.");
        currentHypotheses = evolverResult.ToString() ?? "";
        Console.WriteLine(currentHypotheses);
    }
    
    var summary = $"## Exploration Complete\n\n" +
        $"### Final Hypotheses\n{currentHypotheses}\n\n" +
        $"### Last Critique\n{lastCritique}\n\n" +
        "These are AI-generated hypotheses requiring human validation.";

    return new AgentResponse([new ChatMessage(ChatRole.Assistant, summary)]);
}

// Build the coordinator agent — its middleware handles the entire exploration cycle
// The innerAgent is never actually called (the middleware handles everything),
// but we need a base agent to attach middleware to.
AIAgent explorationAgent = new ChatClientAgent(chatClient,
        name: "ExplorationCoordinator",
        instructions: "You coordinate research exploration.")
    .AsBuilder()
    .Use(runFunc: ExplorationMiddleware, runStreamingFunc: null)
    .Build();

var result = await explorationAgent.RunAsync(
    "What are novel approaches to reducing antibiotic resistance " +
    "in hospital settings that don't involve developing new antibiotics?");

Console.WriteLine($"\n{'-',0}--- Final Discovery Summary ---\n");
Console.WriteLine(result);