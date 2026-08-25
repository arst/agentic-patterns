using System.ClientModel;
using System.Text.Json;
using Azure.AI.OpenAI;
using ConfidenceReporting.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Shared;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

var chatClient = Settings.ChatClient;

var question = "What is the capital of Australia?";

const int runs = 5;

// Agent for self-reported confidence — structured output enforces the shape.
// It scores a CANDIDATE answer, it does not generate its own.
AIAgent selfReportAgent = new ChatClientAgent(chatClient, name: "SelfReporter",
    instructions: """
                  You are a helpful assistant. You will be given a question and a candidate
                  answer. Report your honest confidence between 0.0 and 1.0 that the candidate
                  answer is correct, and give one sentence of reasoning.
                  """);

// For logprobs we need the raw OpenAI ChatClient (IChatClient doesn't expose logprobs)
var openAiChatClient = new AzureOpenAIClient(
        new Uri(Settings.AzureOpenAi.Endpoint),
        new ApiKeyCredential(Settings.AzureOpenAi.ApiKey))
    .GetChatClient(Settings.AzureOpenAi.ChatModelDeployment);

await RunConfidencePipelineAsync(question);

// Every signal below scores the SAME canonical candidate, instead of three signals each
// describing a possibly-different completion.
async Task RunConfidencePipelineAsync(string q)
{
    Console.WriteLine($"Question: {q}\n");

    // 1) ONE canonical candidate, from the raw completion - the only call whose logprobs describe
    //    the exact text we are about to display.
    var completionOptions = new ChatCompletionOptions { IncludeLogProbabilities = true, TopLogProbabilityCount = 1 };
    var completion = await openAiChatClient.CompleteChatAsync(
        [new SystemChatMessage("Answer the question in one short sentence. Be direct."),
         new UserChatMessage(q)], completionOptions);
    var candidate = completion.Value.Content[0].Text.Trim();

    // 2) Logprob signal for THAT candidate.
    var tokens = completion.Value.ContentTokenLogProbabilities;
    var logprobScore = tokens is null or { Count: 0 }
        ? 0.5
        : UncertaintySignals.NormalizeLogprob(
            tokens.Where(t => t.LogProbability > -100).Average(t => t.LogProbability));

    // 3) Self-report ABOUT the candidate, not a fresh answer.
    var selfReport = (await selfReportAgent.RunAsync<SelfReportedResponse>(
        $"Question: {q}\nCandidate answer: {candidate}\n" +
        "Report your confidence between 0.0 and 1.0 that the candidate answer is correct, and one " +
        "sentence of reasoning. Do not restate or replace the candidate answer.")).Result;

    // 4) Consistency = do independent samples AGREE with the candidate? Decided by an equivalence
    //    probe at temperature 0, not by shared long words.
    var agreements = await Task.WhenAll(Enumerable.Range(0, runs).Select(async _ =>
    {
        var sample = (await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "Answer the question in one short sentence. Be direct."),
             new ChatMessage(ChatRole.User, q)],
            new ChatOptions { Temperature = 0.9f })).Text.Trim();
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
    var r = await chatClient.GetResponseAsync(
        [new ChatMessage(ChatRole.User,
            $$"""
              Do these two answers to the same question assert the same thing?
              A: {{candidate}}
              B: {{sample}}
              Respond JSON: {"equivalent": true} or {"equivalent": false}.
              """)],
        new ChatOptions { Temperature = 0f, ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.Json });

    // Fail closed: an unparseable judgement is NOT agreement.
    return JsonSerializer.Deserialize<Dictionary<string, bool>>(r.Text,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))?.GetValueOrDefault("equivalent") == true;
}
