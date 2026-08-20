using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Shared;

var kernel = Settings.Kernel;
var chat = kernel.GetRequiredService<IChatCompletionService>();

// Each agent has its own system prompt and temperature.
// Diversity in both dimensions produces better consensus signal
// than running N identical agents.

var agents = new[]
{
    new AgentConfig(
        "Analyst",
        "You are a precise analytical reasoner. Evaluate the question step by step and give a definitive answer. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it.",
        0.1f // Low — deterministic, analytical
    ),
    new AgentConfig(
        "Generalist",
        "You are a knowledgeable generalist. Think broadly and answer confidently. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it.",
        0.4f // Medium — balanced
    ),
    new AgentConfig(
        "Devil's Advocate",
        "You are a critical thinker who challenges assumptions. Consider alternative perspectives before settling on an answer. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it.",
        0.6f // Higher — more exploratory
    ),
    new AgentConfig(
        "Specialist",
        "You are a domain expert. Use precise, technical reasoning and give a definitive answer. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it.",
        0.2f
    ),
    new AgentConfig(
        "Synthesiser",
        "You are an integrative thinker who considers multiple angles before concluding. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it.",
        0.5f
    )
};

var categoricalTask = "What is the capital of Australia? Answer with just the city name.";
var openEndedTask =
    "What are the most important factors to consider when choosing a cloud provider for a production AI workload?";

Console.WriteLine("=== Democratic Coordination (Semantic Kernel) ===\n");

Console.WriteLine("Categorical Task (Majority Vote)\n");
Console.WriteLine($"Task: {categoricalTask}\n");
var categoricalResult = await RunDemocraticAsync(
    chat, agents, categoricalTask, ConsensusMode.MajorityVote);
PrintResult(categoricalResult);

Console.WriteLine("\nOpen-Ended Task (Synthesis LLM)\n");
Console.WriteLine($"Task: {openEndedTask}\n");
var openEndedResult = await RunDemocraticAsync(
    chat, agents, openEndedTask, ConsensusMode.SynthesisLLM);
PrintResult(openEndedResult);

async Task<CoordinationResult> RunDemocraticAsync(
    IChatCompletionService svc,
    AgentConfig[] agentPool,
    string task,
    ConsensusMode mode)
{
    // All agents reason independently in parallel
    // Each gets its own ChatHistory — no cross-contamination.
    // Task.WhenAll ensures genuine parallelism; the slowest
    // agent gates the result, so a real timeout caps the wait.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

    Console.WriteLine($"Running {agentPool.Length} agents in parallel...\n");

    var agentTasks = agentPool.Select(async agent =>
    {
        var history = new ChatHistory();
        history.AddSystemMessage(agent.SystemPrompt);
        history.AddUserMessage(task);

        // Structured output — the connector enforces the Vote schema
        var settings = new AzureOpenAIPromptExecutionSettings
        {
            Temperature = agent.Temperature,
            ResponseFormat = typeof(Vote)
        };

        try
        {
            var response = await svc.GetChatMessageContentAsync(
                history, settings, cancellationToken: cts.Token);
            var vote = JsonSerializer.Deserialize<Vote>(response.Content ?? "{}");
            if (vote is null) return null;

            Console.WriteLine(
                $"[{agent.Name}] ({agent.Temperature:F1}°, confidence {vote.Confidence:F2}): {vote.Answer} — {vote.Reasoning}\n");

            return new AgentVote(
                agent.Name,
                $"Answer: {vote.Answer}\nReasoning: {vote.Reasoning}",
                vote.Answer,
                vote.Confidence
            );
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[{agent.Name}] Timed out — excluded from vote.\n");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{agent.Name}] Failed ({ex.Message}) — excluded from vote.\n");
            return null;
        }
    });

    var votes = (await Task.WhenAll(agentTasks))
        .Where(v => v != null)
        .Cast<AgentVote>()
        .ToList();

    Console.WriteLine($"Votes collected: {votes.Count}/{agentPool.Length}\n");

    // No votes at all -> abstain explicitly instead of crashing in a consensus mechanism
    if (votes.Count == 0)
        return new CoordinationResult(
            task, "No answer — every voter failed or timed out.",
            0, mode, [], "! Abstained — no votes cast");

    // Apply consensus mechanism
    return mode switch
    {
        ConsensusMode.MajorityVote => ApplyMajorityVote(votes, task),
        ConsensusMode.WeightedVote => ApplyWeightedVote(votes, task),
        ConsensusMode.SynthesisLLM => await ApplySynthesisLLMAsync(svc, votes, task),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}


// CONSENSUS MECHANISM 1 — Majority Vote
// Most common extracted answer wins.
// Vote distribution gives a confidence signal:
//   Unanimous (5/5) → very high confidence
//   Split (3/5)     → moderate confidence
//   Tied (2/2/1)    → low confidence, consider escalation

CoordinationResult ApplyMajorityVote(List<AgentVote> votes, string task)
{
    var groups = votes
        .GroupBy(v => v.ExtractedAnswer.ToLowerInvariant().Trim())
        .OrderByDescending(g => g.Count())
        .ToList();

    var winner = groups.First();
    var totalVotes = votes.Count;
    var confidence = (double)winner.Count() / totalVotes;

    Console.WriteLine("Vote distribution:");
    foreach (var g in groups)
        Console.WriteLine($"  '{g.Key}': {g.Count()}/{totalVotes} votes");

    // Flag low-confidence outcomes for human review
    var flag = confidence < 0.6
        ? "! Split vote — consider human review or synthesis LLM"
        : confidence == 1.0
            ? "V Unanimous"
            : "V Majority";

    return new CoordinationResult(
        task,
        winner.Key,
        confidence,
        ConsensusMode.MajorityVote,
        groups.ToDictionary(g => g.Key, g => g.Count()),
        flag
    );
}

// CONSENSUS MECHANISM 2 — Weighted Vote
// Agents that express higher confidence get more weight.
// Useful when agents have different track records or when
// you want to downweight uncertain responses automatically.

CoordinationResult ApplyWeightedVote(List<AgentVote> votes, string task)
{
    var weightedGroups = votes
        .GroupBy(v => v.ExtractedAnswer.ToLowerInvariant().Trim())
        .Select(g => new
        {
            Answer = g.Key,
            TotalWeight = g.Sum(v => v.Confidence),
            VoteCount = g.Count()
        })
        .OrderByDescending(g => g.TotalWeight)
        .ToList();

    var winner = weightedGroups.First();
    var totalWeight = votes.Sum(v => v.Confidence);
    var confidence = totalWeight > 0 ? winner.TotalWeight / totalWeight : 0;

    Console.WriteLine("Weighted vote distribution:");
    foreach (var g in weightedGroups)
        Console.WriteLine($"  '{g.Answer}': weight {g.TotalWeight:F2} ({g.VoteCount} votes)");

    return new CoordinationResult(
        task,
        winner.Answer,
        confidence,
        ConsensusMode.WeightedVote,
        weightedGroups.ToDictionary(g => g.Answer, g => g.VoteCount),
        confidence >= 0.7 ? "V Clear weighted winner" : "! Close weighted result"
    );
}

// CONSENSUS MECHANISM 3 — Synthesis LLM
// A dedicated LLM reads all agent responses and synthesises
// a final answer. Best for open-ended, free-text outputs
// where majority voting is not meaningful.
// Run this asynchronously / offline for cost efficiency.

async Task<CoordinationResult> ApplySynthesisLLMAsync(
    IChatCompletionService svc,
    List<AgentVote> votes,
    string task)
{
    var responsesBlock = string.Join("\n\n", votes.Select((v, i) =>
        $"Agent {i + 1} ({v.AgentName}):\n{v.FullResponse}"));

    var synthesisHistory = new ChatHistory();
    synthesisHistory.AddSystemMessage("""
                                      You are a synthesis expert. You will receive multiple independent agent responses
                                      to the same task. Your job is to:
                                      1. Identify points of agreement and disagreement.
                                      2. Weigh the quality and reasoning of each response.
                                      3. Produce a single, authoritative final answer that integrates the best insights.
                                      4. Note any significant disagreements that might warrant human review.
                                      Be concise and direct.
                                      """);

    synthesisHistory.AddUserMessage(
        $"""
         Task: {task}

         Agent responses:
         {responsesBlock}

         Synthesise these into a single authoritative answer.
         """);

    var settings = new AzureOpenAIPromptExecutionSettings { Temperature = 0.2f };
    var response = await svc.GetChatMessageContentAsync(synthesisHistory, settings);
    var synthesis = response.Content ?? "";

    Console.WriteLine($"Synthesis LLM output:\n{synthesis}\n");

    return new CoordinationResult(
        task,
        synthesis,
        // A synthesized free-text answer has no vote-derived confidence — don't invent one
        double.NaN,
        ConsensusMode.SynthesisLLM,
        votes.ToDictionary(v => v.AgentName, _ => 1),
        "V Synthesised from all agents"
    );
}

void PrintResult(CoordinationResult result)
{
    Console.WriteLine($"=== Result ({result.Mode}) ===");
    Console.WriteLine($"Final answer: {result.FinalAnswer}");
    Console.WriteLine($"Confidence:   {(double.IsNaN(result.Confidence) ? "n/a (synthesis)" : result.Confidence.ToString("P0"))}");
    Console.WriteLine($"Status:       {result.Flag}\n");
}