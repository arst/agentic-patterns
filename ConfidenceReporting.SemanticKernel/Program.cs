using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using Shared;

var kernel = Settings.Kernel;
var chatService = kernel.GetRequiredService<IChatCompletionService>();

var question = "What is the capital of Australia?";

const int runs = 5;

await RunConfidencePipelineAsync(question);

// Every signal below scores the SAME canonical candidate, instead of three signals each
// describing a possibly-different completion.
async Task RunConfidencePipelineAsync(string q)
{
    Console.WriteLine($"Question: {q}\n");

    // 1) ONE canonical candidate, from the raw completion - the only call whose logprobs describe
    //    the exact text we are about to display.
    var candidateHistory = new ChatHistory();
    candidateHistory.AddSystemMessage("Answer the question in one short sentence. Be direct.");
    candidateHistory.AddUserMessage(q);

    var candidateSettings = new OpenAIPromptExecutionSettings { Logprobs = true, TopLogprobs = 1 };
    var candidateResponse = await chatService.GetChatMessageContentAsync(candidateHistory, candidateSettings);
    var candidate = candidateResponse.Content?.Trim() ?? "";

    // 2) Logprob signal for THAT candidate. SK doesn't surface logprobs natively — reach into the
    //    raw OpenAI ChatCompletion via InnerContent.
    var logprobScore = 0.5;
    if (candidateResponse.InnerContent is ChatCompletion completion)
    {
        var tokens = completion.ContentTokenLogProbabilities;
        if (tokens is { Count: > 0 })
            logprobScore = UncertaintySignals.NormalizeLogprob(
                tokens.Where(t => t.LogProbability > -100).Average(t => t.LogProbability));
    }

    // 3) Self-report ABOUT the candidate, not a fresh answer.
    var selfReportHistory = new ChatHistory();
    selfReportHistory.AddSystemMessage("""
                                        You are a helpful assistant. You will be given a question and a candidate
                                        answer. Report your honest confidence between 0.0 and 1.0 that the candidate
                                        answer is correct, and give one sentence of reasoning.
                                        """);
    selfReportHistory.AddUserMessage(
        $"Question: {q}\nCandidate answer: {candidate}\n" +
        "Report your confidence between 0.0 and 1.0 that the candidate answer is correct, and one " +
        "sentence of reasoning. Do not restate or replace the candidate answer.");

    var selfReportSettings = new AzureOpenAIPromptExecutionSettings { ResponseFormat = typeof(SelfReportedResponse) };
    var selfReportResponse = await chatService.GetChatMessageContentAsync(selfReportHistory, selfReportSettings);
    var selfReport = JsonSerializer.Deserialize<SelfReportedResponse>(selfReportResponse.Content ?? "{}")
                     ?? new SelfReportedResponse();

    // 4) Consistency = do independent samples AGREE with the candidate? Decided by an equivalence
    //    probe at temperature 0, not by shared long words.
    var agreements = await Task.WhenAll(Enumerable.Range(0, runs).Select(async _ =>
    {
        var sampleHistory = new ChatHistory();
        sampleHistory.AddSystemMessage("Answer the question in one short sentence. Be direct.");
        sampleHistory.AddUserMessage(q);

        var sampleResponse = await chatService.GetChatMessageContentAsync(sampleHistory,
            new OpenAIPromptExecutionSettings { Temperature = 0.9 });
        var sample = sampleResponse.Content?.Trim() ?? "";
        return await AgreesAsync(candidate, sample);
    }));
    var consistency = agreements.Count(a => a) / (double)runs;

    var hedging = new[] { "might", "maybe", "possibly", "unclear", "not sure", "i think",
                          "approximately", "perhaps" }
        .Any(w => candidate.Contains(w, StringComparison.OrdinalIgnoreCase));

    var score = UncertaintySignals.RiskScore(
        Math.Clamp(selfReport.Confidence, 0.0, 1.0), logprobScore, consistency, hedging);

    Console.WriteLine($"Answer:                       {candidate}");
    Console.WriteLine($"  self-reported confidence    {selfReport.Confidence:P0}  (subjective, UX hint only)");
    Console.WriteLine($"  token-probability signal    {logprobScore:P0}  (about this exact text)");
    Console.WriteLine($"  agreement across {runs} runs      {consistency:P0}  (equivalence-checked, not keyword overlap)");
    Console.WriteLine($"  hedging language            {(hedging ? "yes" : "no")}");
    Console.WriteLine();
    Console.WriteLine($"Heuristic uncertainty score: {score:F2} -> {UncertaintySignals.Label(score)}");
    Console.WriteLine("This is NOT an estimated probability of correctness. The weights and thresholds");
    Console.WriteLine("are hand-picked and have not been calibrated against labelled data.");
}

// Equivalence probe: do two answers to the same question assert the same thing? Decided by the
// model at temperature 0, not by naive keyword overlap.
async Task<bool> AgreesAsync(string candidate, string sample)
{
    var history = new ChatHistory();
    history.AddUserMessage(
        $$"""
          Do these two answers to the same question assert the same thing?
          A: {{candidate}}
          B: {{sample}}
          Respond JSON: {"equivalent": true} or {"equivalent": false}.
          """);

    var settings = new AzureOpenAIPromptExecutionSettings
    {
        Temperature = 0,
        ResponseFormat = typeof(EquivalenceResponse)
    };
    var response = await chatService.GetChatMessageContentAsync(history, settings);

    // Fail closed: an unparseable judgement is NOT agreement.
    try
    {
        return JsonSerializer.Deserialize<EquivalenceResponse>(response.Content ?? "{}")?.Equivalent == true;
    }
    catch (JsonException)
    {
        return false;
    }
}
