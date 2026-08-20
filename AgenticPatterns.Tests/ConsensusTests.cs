using Voting.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class ConsensusTests
{
    private static AgentVote MakeVote(string answer, double confidence = 0.8, string name = "Voter") =>
        new(name, $"Answer: {answer}", answer, confidence);

    [Fact]
    public void ZeroVotes_Abstains_InsteadOfThrowing()
    {
        var result = Consensus.AbstainIfEmpty([], "task", ConsensusMode.MajorityVote);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Confidence);
        Assert.Contains("Abstained", result.Flag);
    }

    [Fact]
    public void NonEmptyVotes_DoNotAbstain()
    {
        Assert.Null(Consensus.AbstainIfEmpty([MakeVote("canberra")], "task", ConsensusMode.MajorityVote));
    }

    [Fact]
    public void MajorityVote_PicksMostCommonAnswer_WithShareAsConfidence()
    {
        var result = Consensus.MajorityVote(
            [MakeVote("Canberra"), MakeVote("canberra "), MakeVote("Sydney")], "task");

        Assert.Equal("canberra", result.FinalAnswer);
        Assert.Equal(2.0 / 3.0, result.Confidence, precision: 6);
    }

    [Fact]
    public void WeightedVote_PicksHighestTotalConfidence()
    {
        // Two low-confidence votes for Sydney vs one high-confidence vote for Canberra
        var result = Consensus.WeightedVote(
            [MakeVote("Sydney", 0.3), MakeVote("Sydney", 0.3), MakeVote("Canberra", 0.9)], "task");

        Assert.Equal("canberra", result.FinalAnswer);
    }
}
