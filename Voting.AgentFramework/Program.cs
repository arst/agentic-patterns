// In MAF, each agent is a named, first-class entity.
// This makes logs and telemetry clearly attributable.
// Diversity in instructions AND temperature is key — identical
// agents voting produce correlated, not independent, signal.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

var voterAgents = new[]
{
    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Analyst",
        instructions:
        "You are a precise analytical reasoner. Evaluate the question step by step and give a definitive answer. End with 'ANSWER: <your answer>' on the last line."
    ), Temperature: 0.1f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Generalist",
        instructions:
        "You are a knowledgeable generalist. Think broadly and answer confidently. End with 'ANSWER: <your answer>' on the last line."
    ), Temperature: 0.4f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "DevilsAdvocate",
        instructions:
        "You are a critical thinker who challenges assumptions. Consider alternatives before concluding. End with 'ANSWER: <your answer>' on the last line."
    ), Temperature: 0.6f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Specialist",
        instructions:
        "You are a domain expert. Use precise, technical reasoning. End with 'ANSWER: <your answer>' on the last line."
    ), Temperature: 0.2f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Integrator",
        instructions:
        "You are an integrative thinker who weighs multiple angles before concluding. End with 'ANSWER: <your answer>' on the last line."
    ), Temperature: 0.5f)
};

// Dedicated synthesis agent — separate from the voter pool.
// Could point this at a more capable model if needed.
AIAgent synthesisAgent = new ChatClientAgent(
    Settings.ChatClient,
    name: "SynthesisAgent",
    instructions: """
                  You are a synthesis expert. You will receive multiple independent agent responses
                  to the same task. Your job is to:
                  1. Identify points of agreement and disagreement.
                  2. Weigh the quality and reasoning of each response.
                  3. Produce a single, authoritative final answer integrating the best insights.
                  4. Note significant disagreements that might warrant human review.
                  Be concise and direct.
                  """
);

var categoricalTask = "What is the capital of Australia? Answer with just the city name.";
var openEndedTask =
    "What are the most important factors to consider when choosing a cloud provider for a production AI workload?";

Console.WriteLine("=== Democratic Coordination ===\n");

Console.WriteLine("Categorical Task (Majority Vote)\n");
Console.WriteLine($"Task: {categoricalTask}\n");
var categoricalResult = await RunDemocraticAsync(
    voterAgents, synthesisAgent, categoricalTask, ConsensusMode.MajorityVote);
PrintResult(categoricalResult);

Console.WriteLine("\n── Open-Ended Task (Synthesis LLM) ──\n");
Console.WriteLine($"Task: {openEndedTask}\n");
var openEndedResult = await RunDemocraticAsync(
    voterAgents, synthesisAgent, openEndedTask, ConsensusMode.SynthesisLLM);
PrintResult(openEndedResult);

async Task<CoordinationResult> RunDemocraticAsync(
    (ChatClientAgent Agent, float Temperature)[] pool,
    AIAgent synthAgent,
    string task,
    ConsensusMode mode,
    CancellationToken ct = default)
{
    // STEP 1: All agents reason in parallel
    // MAF agents are stateless — the same instance is safe to
    // call concurrently. Per-run temperature via ChatClientAgentRunOptions.

    Console.WriteLine($"Running {pool.Length} agents in parallel...\n");

    var agentTasks = pool.Select(async entry =>
    {
        var (agent, temperature) = entry;

        // Per-run options — temperature set here, not at construction time
        var runOptions = new ChatClientAgentRunOptions(
            new ChatOptions { Temperature = temperature });

        try
        {
            var response = await agent.RunAsync(task, options: runOptions,
                cancellationToken: ct);
            var text = response.Text ?? "";

            Console.WriteLine($"[{agent.Name}] ({temperature:F1}°): {text}\n");

            return new AgentVote(
                agent.Name!,
                text,
                ExtractAnswer(text),
                ExtractConfidence(text)
            );
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[{agent.Name}] Timed out — excluded.\n");
            return null;
        }
    });

    var votes = (await Task.WhenAll(agentTasks))
        .Where(v => v != null)
        .Cast<AgentVote>()
        .ToList();

    Console.WriteLine($"Votes collected: {votes.Count}/{pool.Length}\n");

    //STEP 2: Apply consensus mechanism
    return mode switch
    {
        ConsensusMode.MajorityVote => ApplyMajorityVote(votes, task),
        ConsensusMode.WeightedVote => ApplyWeightedVote(votes, task),
        ConsensusMode.SynthesisLLM => await ApplySynthesisAsync(synthAgent, votes, task),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

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

    var flag = confidence == 1.0 ? "V Unanimous"
        : confidence >= 0.6 ? "V Majority"
        : "! Split — consider synthesis LLM or human review";

    return new CoordinationResult(
        task,
        winner.Key,
        confidence,
        ConsensusMode.MajorityVote,
        groups.ToDictionary(g => g.Key, g => g.Count()),
        flag
    );
}

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

// In MAF, the synthesis step is a dedicated named agent,
// making it easy to swap to a different model, add middleware,
// or route to a human-in-the-loop approval step.
async Task<CoordinationResult> ApplySynthesisAsync(
    AIAgent synthAgent,
    List<AgentVote> votes,
    string task)
{
    var responsesBlock = string.Join("\n\n", votes.Select((v, i) =>
        $"Agent {i + 1} ({v.AgentName}):\n{v.FullResponse}"));

    // Low temperature for synthesis — we want integration, not creativity
    var synthOptions = new ChatClientAgentRunOptions(
        new ChatOptions { Temperature = 0.2f });

    var response = await synthAgent.RunAsync(
        $"""
         Task: {task}

         Agent responses:
         {responsesBlock}

         Synthesise these into a single authoritative answer.
         """,
        options: synthOptions
    );

    var synthesis = response.Text ?? "";
    Console.WriteLine($"Synthesis Agent output:\n{synthesis}\n");

    return new CoordinationResult(
        task,
        synthesis,
        1.0,
        ConsensusMode.SynthesisLLM,
        votes.ToDictionary(v => v.AgentName, _ => 1),
        "V Synthesised from all agents"
    );
}

string ExtractAnswer(string response)
{
    var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var answerLine = lines.LastOrDefault(l =>
        l.StartsWith("ANSWER:", StringComparison.OrdinalIgnoreCase));

    return answerLine != null
        ? answerLine["ANSWER:".Length..].Trim()
        : response.Split('.')[0].Trim();
}

double ExtractConfidence(string response)
{
    var lower = response.ToLowerInvariant();
    if (lower.Contains("certainly") || lower.Contains("definitely")) return 0.95;
    if (lower.Contains("i believe") || lower.Contains("i think")) return 0.65;
    if (lower.Contains("possibly") || lower.Contains("might")) return 0.40;
    if (lower.Contains("unclear") || lower.Contains("not sure")) return 0.25;
    return 0.75;
}

void PrintResult(CoordinationResult result)
{
    Console.WriteLine($"=== Result ({result.Mode}) ===");
    Console.WriteLine($"Final answer: {result.FinalAnswer}");
    Console.WriteLine($"Confidence:   {result.Confidence:P0}");
    Console.WriteLine($"Status:       {result.Flag}\n");
}