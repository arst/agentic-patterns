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

    private static Trial InA(Preference verdict) => new(ReferenceInPositionA: true, verdict);
    private static Trial InB(Preference verdict) => new(ReferenceInPositionA: false, verdict);

    [Fact]
    public void SummarizeCountsWinsAndIndeterminates()
    {
        var report = JudgeParsing.Summarize([
            InA(Preference.A),             // reference in A, judge picks A -> reference wins
            InA(Preference.B),             // reference in A, judge picks B -> other wins
            InA(Preference.Indeterminate),
            InB(Preference.B),             // reference in B, judge picks B -> reference wins
            InB(Preference.B)
        ]);

        Assert.Equal(3, report.ReferenceWins);
        Assert.Equal(1, report.OtherWins);
        Assert.Equal(1, report.Indeterminate);
    }

    [Fact]
    public void JudgeThatAlwaysPicksTheSameSlot_IsFullPositionBias()
    {
        var report = JudgeParsing.Summarize([
            InA(Preference.A), InA(Preference.A), InB(Preference.A), InB(Preference.A)
        ]);

        // Reference wins 100% of the time it sits in A, 0% of the time it sits in B.
        Assert.Equal(1.0, report.PositionSwing);
    }

    [Fact]
    public void JudgeThatIsSimplyWrong_IsNotPositionBias()
    {
        // The judge prefers the weaker candidate every single time, in both slots. That is a bad
        // judge, not a position-dependent one - the pre-fix rate reported this as 100% bias
        // because it folded the slot away before forming the statistic.
        var report = JudgeParsing.Summarize([
            InA(Preference.B), InA(Preference.B), InB(Preference.A), InB(Preference.A)
        ]);

        Assert.Equal(4, report.OtherWins);
        Assert.Equal(0.0, report.PositionSwing);
    }

    [Fact]
    public void ConsistentJudge_HasNoPositionSwing()
    {
        var report = JudgeParsing.Summarize([
            InA(Preference.A), InA(Preference.A), InB(Preference.B), InB(Preference.B)
        ]);

        Assert.Equal(0.0, report.PositionSwing);
    }

    [Fact]
    public void IdenticalOutcomesInDifferentSlotsProduceDifferentStatistics()
    {
        // The reference candidate wins every trial in both runs - identical outcomes - but one run
        // never left slot A. The pre-fix rate folded the slot away before forming the number and
        // so reported the same value for both, making the randomisation do no work at all.
        var oneSlot = JudgeParsing.Summarize([InA(Preference.A), InA(Preference.A), InA(Preference.A)]);
        var bothSlots = JudgeParsing.Summarize([InA(Preference.A), InB(Preference.B), InA(Preference.A)]);

        Assert.Equal(oneSlot.ReferenceWins, bothSlots.ReferenceWins);
        Assert.NotEqual(oneSlot.PositionSwing, bothSlots.PositionSwing);
    }

    [Fact]
    public void SwingIsUnmeasurableWhenOnlyOneSlotWasSampled()
    {
        // Five coin flips land all five trials in one slot 6.25% of the time; Program.cs uses a
        // balanced 3/2 shuffle so this cannot happen there, but the statistic still refuses to
        // invent a position measurement from a single slot.
        var report = JudgeParsing.Summarize([InA(Preference.A), InA(Preference.B), InA(Preference.A)]);

        Assert.Null(report.PositionSwing);
    }

    [Fact]
    public void IndeterminateVerdictsAreExcludedFromTheSwing()
    {
        // Both slots: every determinate verdict is a reference win, so both rates are 1 and the
        // swing is 0. The unparseable verdicts are piled onto slot B only - count them in the
        // denominator and slot B's rate drops to 1/3, inventing a swing out of noise.
        var report = JudgeParsing.Summarize([
            InA(Preference.A),
            InB(Preference.B), InB(Preference.Indeterminate), InB(Preference.Indeterminate)
        ]);

        Assert.Equal(2, report.Indeterminate);
        Assert.Equal(0.0, report.PositionSwing);
    }

    [Fact]
    public void SwingIsUnmeasurableWhenEverythingIsIndeterminate()
    {
        var report = JudgeParsing.Summarize([
            InA(Preference.Indeterminate), InB(Preference.Indeterminate), InA(Preference.Indeterminate)
        ]);

        Assert.Equal(3, report.Indeterminate);
        Assert.Null(report.PositionSwing);
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
