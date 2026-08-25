using ConfidenceReporting.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class UncertaintySignalTests
{
    [Theory]
    [InlineData(-0.05, 0.98)]   // near-certain tokens
    [InlineData(-3.0, 0.0)]     // very uncertain tokens
    [InlineData(-10.0, 0.0)]    // clamped, never negative
    [InlineData(0.0, 1.0)]      // clamped, never above 1
    public void NormalizeLogprobStaysInRange(double average, double expected) =>
        Assert.Equal(expected, UncertaintySignals.NormalizeLogprob(average), 2);

    [Fact]
    public void HedgingLowersTheRiskScore()
    {
        var plain = UncertaintySignals.RiskScore(0.9, 0.9, 0.9, hedging: false);
        var hedged = UncertaintySignals.RiskScore(0.9, 0.9, 0.9, hedging: true);
        Assert.True(hedged < plain);
    }

    [Fact]
    public void LabelNeverClaimsAProbability()
    {
        foreach (var score in new[] { 0.0, 0.5, 0.99 })
            Assert.DoesNotContain("probability", UncertaintySignals.Label(score),
                StringComparison.OrdinalIgnoreCase);
    }
}
