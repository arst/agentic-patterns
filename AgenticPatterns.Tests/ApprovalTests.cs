using Xunit;

namespace AgenticPatterns.Tests;

public class ApprovalTests
{
    [Theory]
    [InlineData(null)] // EOF must never approve
    [InlineData("")]
    [InlineData("n")]
    [InlineData("sure")]
    public void FailClosed_Denies(string? input)
    {
        Assert.False(Approval.Approved(input));
    }

    [Theory]
    [InlineData("y")]
    [InlineData("yes")]
    [InlineData(" YES ")]
    public void ExplicitYes_Approves(string input)
    {
        Assert.True(Approval.Approved(input));
    }
}
