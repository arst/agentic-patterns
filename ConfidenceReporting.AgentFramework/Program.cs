using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Shared;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

var chatClient = Settings.ChatClient;

var question = "What is the capital of Australia?";

// Agent for self-reported confidence — structured output enforces the shape
AIAgent selfReportAgent = new ChatClientAgent(chatClient, name: "SelfReporter",
    instructions: """
                  You are a helpful assistant. Answer the question, report your honest
                  confidence between 0.0 and 1.0, and give one sentence of reasoning
                  for why you are or aren't confident.
                  """);

// For logprobs we need the raw OpenAI ChatClient (IChatClient doesn't expose logprobs)
var openAiChatClient = new AzureOpenAIClient(
        new Uri(Settings.AzureOpenAi.Endpoint),
        new ApiKeyCredential(Settings.AzureOpenAi.ApiKey))
    .GetChatClient(Settings.AzureOpenAi.ChatModelDeployment);

await RunConfidencePipelineAsync(question);

// Runs all three confidence techniques and prints the combined verdict
async Task RunConfidencePipelineAsync(string q)
{
    Console.WriteLine($"Question: {q}\n");

    var selfReported = await GetSelfReportedConfidenceAsync(q);
    var logprobScore = await GetLogprobConfidenceAsync(q);
    var consistency = await GetConsistencySamplingConfidenceAsync(q);

    var combinedConfidence = CombineConfidence(selfReported, logprobScore, consistency);

    Console.WriteLine("=== Confidence Results ===\n");
    Console.WriteLine($"Answer:                   {selfReported.Answer}");
    Console.WriteLine($"Self-reported confidence: {selfReported.Confidence:P0} (subjective, treat as UX hint)");
    Console.WriteLine($"Logprob confidence:       {logprobScore:P0}            (token probability signal)");
    Console.WriteLine(
        $"Consistency score:        {consistency.Score:P0}        (agreement across {consistency.Runs} runs)");
    Console.WriteLine($"Hedging language:         {(selfReported.ContainsHedging ? "Yes" : "No")}");
    Console.WriteLine();
    Console.WriteLine(
        $"► Combined confidence:    {combinedConfidence:P0}   → {GetConfidenceLabel(combinedConfidence)}");

    Console.WriteLine(
        $"\nAnswer: {selfReported.Answer} (combined confidence: {combinedConfidence:P0})");
}

// ── Self-reported confidence via agent ──────────────────────────────────────

async Task<SelfReportedResult> GetSelfReportedConfidenceAsync(string q)
{
    var agentResult = await selfReportAgent.RunAsync<SelfReportedResponse>(q);
    var parsed = agentResult.Result;

    var hedgingWords = new[]
    {
        "might", "maybe", "possibly", "unclear",
        "not sure", "i think", "approximately", "perhaps"
    };
    var hasHedging = hedgingWords.Any(w =>
        parsed.Answer.Contains(w, StringComparison.OrdinalIgnoreCase));

    return new SelfReportedResult(
        parsed.Answer,
        Math.Clamp(parsed.Confidence, 0f, 1f),
        parsed.Reasoning,
        hasHedging
    );
}

// ── Logprob confidence (token probability) ─────────────────────────────────

async Task<double> GetLogprobConfidenceAsync(string q)
{
    var completionOptions = new ChatCompletionOptions
    {
        IncludeLogProbabilities = true,
        TopLogProbabilityCount = 1
    };

    var completion = await openAiChatClient.CompleteChatAsync(
    [
        new SystemChatMessage("Answer the question as concisely as possible."),
        new UserChatMessage(q)
    ], completionOptions);

    var logprobs = completion.Value.ContentTokenLogProbabilities;
    if (logprobs is null || logprobs.Count == 0)
        return 0.5;

    // Average the per-token log probabilities, then normalise to 0–1.
    // logprob is in natural log: probability = exp(logprob)
    // Typical range: -0.1 (very confident) to -3.0 (very uncertain)
    var avgLogprob = logprobs
        .Where(t => t.LogProbability > -100)
        .Average(t => t.LogProbability);

    var normalised = Math.Clamp((avgLogprob + 3.0) / 3.0, 0.0, 1.0);
    return normalised;
}

// Consistency sampling

async Task<ConsistencyResult> GetConsistencySamplingConfidenceAsync(string q, int runs = 5)
{
    // Run the same question N times at higher temperature to introduce variance
    var tasks = Enumerable.Range(0, runs).Select(async _ =>
    {
        var response = await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "Answer the question in one short sentence. Be direct."),
            new ChatMessage(ChatRole.User, q)
        ], new ChatOptions { Temperature = 0.9f });

        return response.Text.Trim().ToLowerInvariant();
    });

    var results = await Task.WhenAll(tasks);
    var answers = results.Where(r => r.Length > 0).ToList();

    // Find the most common answer
    var mostCommon = answers
        .GroupBy(a => a)
        .OrderByDescending(g => g.Count())
        .First();

    // Score = fraction of runs that agreed with the majority answer (fuzzy match)
    var majorityKeywords = mostCommon.Key.Split(' ')
        .Where(w => w.Length > 4)
        .ToHashSet();

    var agreementCount = answers.Count(a =>
        majorityKeywords.Any(kw => a.Contains(kw)));

    var score = (double)agreementCount / runs;

    return new ConsistencyResult(
        mostCommon.Key,
        score,
        runs,
        answers
    );
}

// Weighted combination

double CombineConfidence(
    SelfReportedResult selfReported,
    double logprobScore,
    ConsistencyResult consistency)
{
    const double wSelf = 0.20;
    const double wLogprob = 0.35;
    const double wConsistency = 0.45;

    var combined =
        selfReported.Confidence * wSelf +
        logprobScore * wLogprob +
        consistency.Score * wConsistency;

    if (selfReported.ContainsHedging)
        combined *= 0.85;

    return Math.Clamp(combined, 0.0, 1.0);
}

string GetConfidenceLabel(double score)
{
    return score switch
    {
        >= 0.85 => "High confidence",
        >= 0.60 => "Medium confidence",
        >= 0.40 => "Low confidence",
        _ => "Very low confidence — consider human review"
    };
}