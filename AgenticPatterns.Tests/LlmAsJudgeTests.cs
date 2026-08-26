using LLMAsJudge.AgentFramework;
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
