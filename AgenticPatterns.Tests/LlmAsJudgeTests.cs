using LLMAsJudge.AgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgenticPatterns.Tests;

public class LlmAsJudgeTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("garbage")] [InlineData("{}")]
    [InlineData("{\"winner\":\"a\"}")] [InlineData("{\"winner\":\"C\"}")]
    public void AnythingUnexpectedIsIndeterminate(string? json) =>
        Assert.Equal(Preference.Indeterminate, JudgeParsing.Parse(json));

    [Fact] public void AParses() => Assert.Equal(Preference.A, JudgeParsing.Parse("{\"winner\":\"A\"}"));
    [Fact] public void BParses() => Assert.Equal(Preference.B, JudgeParsing.Parse("{\"winner\":\"B\"}"));

    // Malformed JSON that still throws on Deserialize (not just "returns null") - the brief's
    // controller ruling: Parse must catch JsonException, not just handle null/missing keys.
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("not json at all {{{")]
    public void MalformedJsonDoesNotThrow(string json) =>
        Assert.Equal(Preference.Indeterminate, JudgeParsing.Parse(json));

    [Theory]
    [InlineData(Preference.A, true, true)]
    [InlineData(Preference.B, true, false)]
    [InlineData(Preference.A, false, false)]
    [InlineData(Preference.B, false, true)]
    public void ResolveTranslatesVerdictAndPositionIntoReferenceWin(
        Preference verdict, bool referenceInPositionA, bool expectedReferenceWon) =>
        Assert.Equal(expectedReferenceWon, JudgeParsing.Resolve(verdict, referenceInPositionA));

    [Fact]
    public void ResolveIsNullForIndeterminate() =>
        Assert.Null(JudgeParsing.Resolve(Preference.Indeterminate, referenceInPositionA: true));

    [Fact]
    public void SummarizeCountsWinsAndIndeterminates()
    {
        var report = JudgeParsing.Summarize([true, false, null, true, true]);

        Assert.Equal(3, report.ReferenceWins);
        Assert.Equal(1, report.OtherWins);
        Assert.Equal(1, report.Indeterminate);
    }

    [Fact]
    public void SummarizeExcludesIndeterminateFromBiasRate()
    {
        // 4 determinate results, 1 of them a flip away from the reference: rate is 1/4, not 1/5.
        var report = JudgeParsing.Summarize([true, true, true, false, null]);

        Assert.Equal(0.25, report.PositionBiasRate);
    }

    [Fact]
    public void SummarizeBiasRateIsZeroWhenAllIndeterminate()
    {
        var report = JudgeParsing.Summarize([null, null, null]);

        Assert.Equal(3, report.Indeterminate);
        Assert.Equal(0.0, report.PositionBiasRate);
    }
}

// Drives the real RubricJudgeEvaluator end to end - a scripted judge reply through
// EvaluateAsync - because the defect lived in how the evaluator reads that reply.
public class RubricJudgeEvaluatorTests
{
    private static async Task<NumericMetric> JudgeSaysAsync(string judgeReply)
    {
        var client = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, judgeReply)));

        var result = await new RubricJudgeEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.User, "What warranty do the laptops come with?")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Two years.")),
            new ChatConfiguration(client));

        return result.Get<NumericMetric>(RubricJudgeEvaluator.RubricScoreMetricName);
    }

    [Theory]
    [InlineData("")]                                  // truncated to nothing
    [InlineData("not json")]                          // prose instead of JSON
    [InlineData("{}")]                                // JSON, but no score
    [InlineData("null")]                              // literal JSON null
    [InlineData("   ")]                               // whitespace only
    [InlineData("[1,2,3]")]                           // JSON of the wrong shape
    [InlineData("{\"score\":0,\"justification\":\"x\"}")]  // below the rubric floor
    [InlineData("{\"score\":9,\"justification\":\"x\"}")]  // above the rubric ceiling
    public async Task UnreadableVerdictIsIndeterminate_NeverThrows_NeverANumber(string judgeReply)
    {
        var metric = await JudgeSaysAsync(judgeReply);

        // Indeterminate is "no value", not 0: 0 is below the rubric's own floor of 1, so scoring
        // an unreadable verdict as a number would rank it worse than the worst possible answer.
        Assert.Null(metric.Value);
        Assert.Contains("Indeterminate", metric.Reason);
    }

    [Fact]
    public async Task ParseableVerdictKeepsScoreAndJustification()
    {
        var metric = await JudgeSaysAsync("{\"score\":4,\"justification\":\"Accurate but terse.\"}");

        Assert.Equal(4, metric.Value);
        Assert.Equal("Accurate but terse.", metric.Reason);
    }

    [Fact]
    public async Task ScoreIsNeverBelowTheRubricFloor()
    {
        foreach (var reply in new[] { "", "not json", "{}", "null", "{\"score\":-3}" })
            Assert.True(await JudgeSaysAsync(reply) is { Value: null or >= 1 });
    }
}
