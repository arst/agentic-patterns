using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace LLMAsJudge.AgentFramework;

// A minimal custom LLM-as-judge evaluator: scores the response 1-5 against a fixed rubric
// and requires a one-sentence justification. Demonstrates writing IEvaluator directly
// rather than only consuming the built-in quality evaluators.
public sealed class RubricJudgeEvaluator : IEvaluator
{
    public const string RubricScoreMetricName = "Rubric Score";
    public IReadOnlyCollection<string> EvaluationMetricNames => [RubricScoreMetricName];

    private sealed record Verdict(int Score, string Justification);

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatConfiguration);
        var question = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        var prompt =
            $$"""
             You are grading a customer-support answer on a 1-5 rubric:
             5 = accurate, complete, on-policy; 3 = partially correct; 1 = wrong or evasive.
             Question: {{question}}
             Answer: {{modelResponse.Text}}
             Respond with JSON: {"score": <1-5>, "justification": "<one sentence>"}.
             """;

        var response = await chatConfiguration.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { Temperature = 0f, ResponseFormat = ChatResponseFormat.Json },
            cancellationToken);

        var verdict = JsonSerializer.Deserialize<Verdict>(response.Text,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new Verdict(0, "Judge returned unparseable output.");

        var metric = new NumericMetric(RubricScoreMetricName, verdict.Score, verdict.Justification);
        return new EvaluationResult(metric);
    }
}
