namespace RegressionEvals.AgentFramework;

// The rule that decides which golden cases are trustworthy enough to gate a release: a case is
// evaluated only once a human has reviewed it (a non-empty ReviewedBy); everything else is
// awaiting review and must never be silently evaluated. This is what stops a trace-derived
// candidate - or any hand-added row missing a reviewer - from freezing an unreviewed answer into
// the suite.
public static class CasePartition
{
    public static (IReadOnlyList<GoldenCase> Evaluated, IReadOnlyList<GoldenCase> AwaitingReview) Partition(
        IEnumerable<GoldenCase> cases)
    {
        var evaluated = new List<GoldenCase>();
        var awaitingReview = new List<GoldenCase>();
        foreach (var c in cases)
            (string.IsNullOrWhiteSpace(c.ReviewedBy) ? awaitingReview : evaluated).Add(c);
        return (evaluated, awaitingReview);
    }

    // A suite that evaluated zero cases is not a passing suite, even if nothing failed - the same
    // class of defect as freezing an unreviewed trace answer into the gate.
    public static int GateExitCode(int evaluatedCount, int failureCount) =>
        evaluatedCount == 0 || failureCount > 0 ? 1 : 0;
}
