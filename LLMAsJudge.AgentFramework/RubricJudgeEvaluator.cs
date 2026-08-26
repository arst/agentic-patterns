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

    // The rubric's own floor and ceiling. A score outside them is not a verdict.
    private const int MinScore = 1;
    private const int MaxScore = 5;

    private sealed record Verdict(int Score, string? Justification);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Never throws, for any input. An empty, truncated, non-JSON, or score-less judge reply is
    /// <c>null</c> — indeterminate — and not a <c>Verdict(0, …)</c>: 0 sits below the rubric's own
    /// floor of 1, so recording an unreadable verdict as a number would score it worse than the
    /// worst possible answer instead of admitting the judge was not understood. Empty and
    /// whitespace input arrive here as a <see cref="JsonException"/> like any other malformed
    /// reply, and <c>"null"</c> deserializes to a null <c>Verdict</c>.
    /// </summary>
    private static Verdict? ParseVerdict(string text)
    {
        Verdict? verdict;
        try
        {
            verdict = JsonSerializer.Deserialize<Verdict>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return verdict is { Score: >= MinScore and <= MaxScore } ? verdict : null;
    }

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

        // An unreadable judge reply is reported as a metric with no value at all, so downstream
        // averages and gates see "not measured" rather than a number the judge never gave.
        var metric = ParseVerdict(response.Text) is { } verdict
            ? new NumericMetric(RubricScoreMetricName, verdict.Score, verdict.Justification)
            : new NumericMetric(RubricScoreMetricName, value: null,
                reason: $"Indeterminate: the judge's reply was not a parseable "
                        + $"{MinScore}-{MaxScore} rubric verdict.");

        return new EvaluationResult(metric);
    }
}
