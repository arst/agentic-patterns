using SelfCorrectionLoop;
using Xunit;

namespace AgenticPatterns.Tests;

public class DraftSelectionTests
{
    [Fact]
    public void Best_KeepsHighestScore_EvenWhenLaterDraftIsWorse()
    {
        var (draft, score) = DraftSelection.Best(
            [("good draft", 0.9), ("worse rewrite", 0.4)], charLimit: 150);

        Assert.Equal("good draft", draft);
        Assert.Equal(0.9, score);
    }

    [Fact]
    public void Best_ExcludesOverLimitDrafts_WhenAFittingOneExists()
    {
        var over = new string('x', 200);

        var (draft, _) = DraftSelection.Best(
            [(over, 0.95), ("fits", 0.5)], charLimit: 150);

        Assert.Equal("fits", draft);
    }

    [Fact]
    public void Best_FallsBackToOverLimit_WhenNothingFits()
    {
        var over = new string('x', 200);

        var (draft, _) = DraftSelection.Best([(over, 0.6)], charLimit: 150);

        Assert.Equal(over, draft);
    }

    [Theory]
    [InlineData("REVISE\nSCORE: 0.7\nToo long.", 0.7)]
    [InlineData("APPROVED\nSCORE: 1.0", 1.0)]
    [InlineData("no score here", 0.0)]
    public void ParseScore_Cases(string feedback, double expected)
    {
        Assert.Equal(expected, DraftSelection.ParseScore(feedback));
    }
}
