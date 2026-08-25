namespace ConfidenceReporting.AgentFramework;

/// Heuristic uncertainty features. NONE of these is a calibrated probability of correctness:
/// the weights, the normalisation window and the thresholds were chosen by hand, not fitted to
/// labelled outcomes. Treat the score as a routing signal (answer / escalate / abstain), never as
/// a number to show a user as "how likely this is right".
public static class UncertaintySignals
{
    // ponytail: hand-picked heuristic, not a calibrated model. Upgrade path is a `--calibrate`
    // mode that runs a labelled question/answer set, records (score, wasCorrect), and reports
    // Brier score, expected calibration error, accuracy per bucket, and selective accuracy.

    // A per-token average logprob typically runs from about -3.0 (uncertain) to 0.0 (certain).
    public static double NormalizeLogprob(double averageLogprob) =>
        Math.Clamp((averageLogprob + 3.0) / 3.0, 0.0, 1.0);

    public static double RiskScore(double selfReported, double logprob, double consistency, bool hedging)
    {
        // ponytail: hand-picked weights. Replace with a logistic model fitted on labelled
        // examples before any of this drives an automated decision.
        var combined = selfReported * 0.20 + logprob * 0.35 + consistency * 0.45;
        if (hedging) combined *= 0.85;
        return Math.Clamp(combined, 0.0, 1.0);
    }

    public static string Label(double score) => score switch
    {
        >= 0.85 => "answer directly",
        >= 0.60 => "answer with the uncertainty shown",
        >= 0.40 => "escalate to a second check",
        _ => "abstain; route to a human"
    };
}
