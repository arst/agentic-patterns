// In MAF, each agent is a named, first-class entity.
// This makes logs and telemetry clearly attributable.
// Diversity in instructions AND temperature is key — identical
// agents voting produce correlated, not independent, signal.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;
using Voting.AgentFramework;

var voterAgents = new[]
{
    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Analyst",
        instructions:
        "You are a precise analytical reasoner. Evaluate the question step by step and give a definitive answer. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it."
    ), Temperature: 0.1f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Generalist",
        instructions:
        "You are a knowledgeable generalist. Think broadly and answer confidently. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it."
    ), Temperature: 0.4f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "DevilsAdvocate",
        instructions:
        "You are a critical thinker who challenges assumptions. Consider alternatives before concluding. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it."
    ), Temperature: 0.6f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Specialist",
        instructions:
        "You are a domain expert. Use precise, technical reasoning. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it."
    ), Temperature: 0.2f),

    (Agent: new ChatClientAgent(
        Settings.ChatClient,
        name: "Integrator",
        instructions:
        "You are an integrative thinker who weighs multiple angles before concluding. Give your final answer, your reasoning, and your honest confidence between 0.0 and 1.0 — do not inflate it."
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
    ConsensusMode mode)
{
    // STEP 1: All agents reason in parallel
    // MAF agents are stateless — the same instance is safe to
    // call concurrently. Per-run temperature via ChatClientAgentRunOptions.

    // Real timeout — slow agents are cancelled and excluded from the vote
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

    Console.WriteLine($"Running {pool.Length} agents in parallel...\n");

    var agentTasks = pool.Select(async entry =>
    {
        var (agent, temperature) = entry;

        // Per-run options — temperature set here, not at construction time
        var runOptions = new ChatClientAgentRunOptions(
            new ChatOptions { Temperature = temperature });

        try
        {
            var response = await agent.RunAsync<Vote>(task, options: runOptions,
                cancellationToken: cts.Token);
            var vote = response.Result;

            Console.WriteLine(
                $"[{agent.Name}] ({temperature:F1}°, confidence {vote.Confidence:F2}): {vote.Answer} — {vote.Reasoning}\n");

            return new AgentVote(
                agent.Name!,
                $"Answer: {vote.Answer}\nReasoning: {vote.Reasoning}",
                vote.Answer,
                vote.Confidence
            );
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[{agent.Name}] Timed out — excluded.\n");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{agent.Name}] Failed ({ex.Message}) — excluded.\n");
            return null;
        }
    });

    var votes = (await Task.WhenAll(agentTasks))
        .Where(v => v != null)
        .Cast<AgentVote>()
        .ToList();

    Console.WriteLine($"Votes collected: {votes.Count}/{pool.Length}\n");

    if (Consensus.AbstainIfEmpty(votes, task, mode) is { } abstained)
        return abstained;

    //STEP 2: Apply consensus mechanism
    return mode switch
    {
        ConsensusMode.MajorityVote => Consensus.MajorityVote(votes, task),
        ConsensusMode.WeightedVote => Consensus.WeightedVote(votes, task),
        ConsensusMode.SynthesisLLM => await ApplySynthesisAsync(synthAgent, votes, task),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
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