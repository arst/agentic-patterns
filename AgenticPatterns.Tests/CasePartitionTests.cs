using RegressionEvals.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class CasePartitionTests
{
    private static GoldenCase Case(string id, string reviewedBy) =>
        new(id, "Q?", "A.", "contains", reviewedBy);

    [Fact]
    public void ReviewedCaseIsEvaluated()
    {
        var (evaluated, awaitingReview) = CasePartition.Partition([Case("reviewed", "alex")]);

        Assert.Equal(["reviewed"], evaluated.Select(c => c.Id));
        Assert.Empty(awaitingReview);
    }

    // This is the guarantee this task exists to pin: a case with no reviewer - exactly the shape
    // ExtractTraceCase used to hand straight to the evaluator - must be EXCLUDED from evaluation,
    // not merely present somewhere.
    [Fact]
    public void UnreviewedCaseIsExcludedFromEvaluationAndReportedAsAwaitingReview()
    {
        var (evaluated, awaitingReview) = CasePartition.Partition([Case("from-trace", reviewedBy: null!)]);

        Assert.Empty(evaluated);
        Assert.Equal(["from-trace"], awaitingReview.Select(c => c.Id));
    }

    [Fact]
    public void EmptyReviewedByIsAlsoAwaitingReview()
    {
        var (evaluated, awaitingReview) = CasePartition.Partition([Case("blank", reviewedBy: "")]);

        Assert.Empty(evaluated);
        Assert.Single(awaitingReview);
    }

    [Fact]
    public void MixedCorpusSplitsCorrectly()
    {
        var (evaluated, awaitingReview) = CasePartition.Partition(
        [
            Case("a", "alex"),
            Case("b", null!),
            Case("c", "jamie"),
            Case("d", "")
        ]);

        Assert.Equal(["a", "c"], evaluated.Select(c => c.Id));
        Assert.Equal(["b", "d"], awaitingReview.Select(c => c.Id));
    }

    [Fact]
    public void GateFailsWhenNothingWasEvaluatedEvenWithZeroFailures() =>
        Assert.Equal(1, CasePartition.GateExitCode(evaluatedCount: 0, failureCount: 0));

    [Fact]
    public void GatePassesWhenSomethingWasEvaluatedAndNothingFailed() =>
        Assert.Equal(0, CasePartition.GateExitCode(evaluatedCount: 3, failureCount: 0));

    [Fact]
    public void GateFailsOnAnyFailure() =>
        Assert.Equal(1, CasePartition.GateExitCode(evaluatedCount: 3, failureCount: 1));
}
