using TreeOfThoughts;
using Xunit;

namespace AgenticPatterns.Tests;

public class Solver24Tests
{
    private static readonly double[] Numbers = [4, 9, 10, 13];

    [Fact]
    public void ValidFullPath_IsAccepted()
    {
        var steps = new[]
        {
            "13 - 9 = 4 (remaining: 4, 10, 4)",
            "10 - 4 = 6 (remaining: 4, 6)",
            "4 * 6 = 24 (remaining: 24)",
            "done: 4 * (10 - (13 - 9)) = 24"
        };

        Assert.True(Solver24.Verify(steps, Numbers, out var reason), reason);
    }

    [Fact]
    public void WrongArithmetic_IsRejected()
    {
        Assert.False(Solver24.Verify(["9 + 10 = 21 (remaining: 4, 13, 21)"], Numbers, out var reason));
        Assert.Contains("not 21", reason);
    }

    [Fact]
    public void DoneWithMoreThanOneRemaining_IsRejected()
    {
        Assert.False(Solver24.Verify(["13 - 9 = 4 (remaining: ...)", "done: fake = 24"], Numbers, out var reason));
        Assert.Contains("'done' claimed", reason);
    }

    [Fact]
    public void DoneWithWrongResult_IsRejected()
    {
        var steps = new[] { "4 + 9 = 13", "13 + 10 = 23", "23 + 13 = 36", "done: whatever = 24" };
        Assert.False(Solver24.Verify(steps, Numbers, out _));
    }

    [Fact]
    public void OperandNotInPool_IsRejected()
    {
        Assert.False(Solver24.Verify(["6 * 4 = 24"], Numbers, out var reason));
        Assert.Contains("not among", reason);
    }

    [Fact]
    public void NumberReuse_IsRejected()
    {
        Assert.False(Solver24.Verify(["4 + 4 = 8"], Numbers, out var reason));
        Assert.Contains("not among", reason);
    }

    [Fact]
    public void UnparseableStep_IsRejected()
    {
        Assert.False(Solver24.Verify(["combine some numbers"], Numbers, out var reason));
        Assert.Contains("unparseable", reason);
    }

    [Fact]
    public void FractionalStep_ParsesCultureInvariantly()
    {
        // Division makes fractional intermediates legitimate; "2.25" must never parse as 225
        Assert.True(Solver24.Verify(["9 / 4 = 2.25 (remaining: 2.25, 10, 13)"], Numbers, out var reason), reason);
    }

    [Fact]
    public void ValidPrefixWithoutDone_IsAccepted()
    {
        Assert.True(Solver24.Verify(["13 - 9 = 4 (remaining: 4, 10, 4)"], Numbers, out _));
    }
}
