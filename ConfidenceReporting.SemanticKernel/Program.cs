using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using Shared;

var kernel = Settings.Kernel;
var chatService = kernel.GetRequiredService<IChatCompletionService>();

var question = "What is the capital of Australia?";

Console.WriteLine($"Question: {question}\n");

var selfReported = await GetSelfReportedConfidenceAsync(chatService, question);
var logprobScore = await GetLogprobConfidenceAsync(chatService, question);
var consistency = await GetConsistencySamplingConfidenceAsync(chatService, question);

var combinedConfidence = CombineConfidence(selfReported, logprobScore, consistency);

Console.WriteLine("=== Confidence Results ===\n");
Console.WriteLine($"Answer:                   {selfReported.Answer}");
Console.WriteLine($"Self-reported confidence: {selfReported.Confidence:P0}(subjective, treat as UX hint)");
Console.WriteLine($"Logprob confidence:       {logprobScore:P0}           (token probability signal)");
Console.WriteLine($"Consistency score:        {consistency.Score:P0}       (agreement across {consistency.Runs} runs)");
Console.WriteLine($"Hedging language:         {(selfReported.ContainsHedging ? "Yes" : "No")}");
Console.WriteLine();
Console.WriteLine($"► Combined confidence:    {combinedConfidence:P0}  → {GetConfidenceLabel(combinedConfidence)}");


// Self-reported confidence via structured output
async Task<SelfReportedResult> GetSelfReportedConfidenceAsync(
    IChatCompletionService svc, string q)
{
    var history = new ChatHistory();
    history.AddSystemMessage("""
                             You are a helpful assistant. Always respond in JSON with this exact schema:
                             {
                               "answer": "<your answer>",
                               "confidence": <float between 0.0 and 1.0>,
                               "reasoning": "<one sentence why you are or aren't confident>"
                             }
                             Return ONLY the JSON object, no markdown, no extra text.
                             """);
    history.AddUserMessage(q);

    // Tell SK we want JSON back
    var settings = new OpenAIPromptExecutionSettings
    {
        ResponseFormat = "json_object"
    };

    var response = await svc.GetChatMessageContentAsync(history, settings);
    var raw = response.Content ?? "{}";

    var parsed = JsonSerializer.Deserialize<SelfReportedResponse>(raw)
                 ?? new SelfReportedResponse();

    // Bonus: detect hedging language in the answer itself
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

// Logprobs (token probability)

async Task<double> GetLogprobConfidenceAsync(
    IChatCompletionService svc, string q)
{
    var history = new ChatHistory();
    history.AddSystemMessage("Answer the question as concisely as possible.");
    history.AddUserMessage(q);

    // Enable logprobs via execution settings
    // TopLogprobs = 1 gives us the top alternative at each token position
    var settings = new OpenAIPromptExecutionSettings
    {
        Logprobs = true,
        TopLogprobs = 1
    };

    var response = await svc.GetChatMessageContentAsync(history, settings);

    // Pull the raw OpenAI ChatCompletion from the inner content
    // to access logprob data (SK doesn't surface this natively)
    if (response.InnerContent is not ChatCompletion completion)
        return 0.5; // fallback if not available

    var logprobContent = completion.ContentTokenLogProbabilities;
    if (logprobContent is null || logprobContent.Count == 0)
        return 0.5;

    // Average the per-token log probabilities, then convert to probability
    // logprob is in natural log, so: probability = exp(logprob)
    var avgLogprob = logprobContent
        .Where(t => t.LogProbability > -100) // filter out <100 (essentially zero)
        .Average(t => t.LogProbability);

    // Map the average log probability to a 0–1 confidence score.
    // Typical range: -0.1 (very confident) to -3.0 (very uncertain)
    // We clamp and normalise into a readable 0–1 range.
    var normalised = Math.Clamp((avgLogprob + 3.0) / 3.0, 0.0, 1.0);
    return normalised;
}

//Consistency sampling

async Task<ConsistencyResult> GetConsistencySamplingConfidenceAsync(
    IChatCompletionService svc, string q, int runs = 5)
{
    var answers = new List<string>();

    // Run the same question N times at higher temperature to introduce variance
    var tasks = Enumerable.Range(0, runs).Select(async _ =>
    {
        var history = new ChatHistory();
        history.AddSystemMessage(
            "Answer the question in one short sentence. Be direct.");
        history.AddUserMessage(q);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.9 // Higher temp = more variance = better signal
        };

        var response = await svc.GetChatMessageContentAsync(history, settings);
        return response.Content?.Trim().ToLowerInvariant() ?? "";
    });

    var results = await Task.WhenAll(tasks);
    answers.AddRange(results.Where(r => r.Length > 0));

    // Find the most common answer
    var mostCommon = answers
        .GroupBy(a => a)
        .OrderByDescending(g => g.Count())
        .First();

    // Score = fraction of runs that agreed with the majority answer
    // Fuzzy match: if the answer contains the majority answer's key terms
    var majorityKeywords = mostCommon.Key.Split(' ')
        .Where(w => w.Length > 4) // skip short words
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

// Weighted combination of all three signals

double CombineConfidence(
    SelfReportedResult selfReported,
    double logprobScore,
    ConsistencyResult consistency)
{
    // Weights — tweak these based on how much you trust each signal:
    // Logprobs are most "objective", consistency is expensive but reliable,
    // self-report is least reliable but adds some signal.
    const double wSelf = 0.20;
    const double wLogprob = 0.35;
    const double wConsistency = 0.45;

    var combined =
        selfReported.Confidence * wSelf +
        logprobScore * wLogprob +
        consistency.Score * wConsistency;

    // Apply a penalty if hedging language was detected
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