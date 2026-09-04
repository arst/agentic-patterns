namespace SelfHealingOperationsLoop.AgentFramework;

public sealed record ServiceHealth(string Version, double P99Milliseconds, double ErrorRate, string Signature);

public sealed record HealingPolicy(
    double MaxP99Milliseconds,
    double MaxErrorRate,
    IReadOnlySet<string> AllowedActions,
    double MinimumConfidence);

public sealed record Diagnosis(string Action, double Confidence, string Evidence);

public enum HealingStatus
{
    Healthy,
    Resolved,
    Escalated
}

public sealed record HealingEvent(string Phase, string Detail);

public sealed record HealingReport(
    HealingStatus Status,
    ServiceHealth Before,
    ServiceHealth? After,
    IReadOnlyList<HealingEvent> Events);

public sealed class SelfHealingLoop
{
    private readonly HealingPolicy policy;

    public SelfHealingLoop(HealingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!double.IsFinite(policy.MaxP99Milliseconds) || !double.IsFinite(policy.MaxErrorRate) ||
            !double.IsFinite(policy.MinimumConfidence) ||
            policy.MaxP99Milliseconds <= 0 || policy.MaxErrorRate is < 0 or > 1 ||
            policy.MinimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(policy));
        this.policy = policy;
    }

    public HealingReport Run(
        ServiceHealth before,
        Diagnosis diagnosis,
        Func<string, ServiceHealth> remediate)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(diagnosis);
        ArgumentNullException.ThrowIfNull(remediate);

        var events = new List<HealingEvent>();
        if (Healthy(before))
            return new(HealingStatus.Healthy, before, before, [new("detect", "SLO is healthy; no action taken.")]);

        events.Add(new("detect", $"SLO breach: p99={before.P99Milliseconds}ms errors={before.ErrorRate:P1}."));
        events.Add(new("diagnose", $"{diagnosis.Action} at {diagnosis.Confidence:P0}: {diagnosis.Evidence}"));

        if (!double.IsFinite(diagnosis.Confidence) || diagnosis.Confidence is < 0 or > 1 ||
            diagnosis.Confidence < policy.MinimumConfidence || !policy.AllowedActions.Contains(diagnosis.Action))
        {
            events.Add(new("escalate", "Diagnosis is outside the automatic-remediation policy."));
            return new(HealingStatus.Escalated, before, null, events);
        }

        try
        {
            events.Add(new("remediate", $"Executing policy-approved action {diagnosis.Action}."));
            var after = remediate(diagnosis.Action);
            if (Healthy(after))
            {
                events.Add(new("verify", $"Recovered: p99={after.P99Milliseconds}ms errors={after.ErrorRate:P1}."));
                return new(HealingStatus.Resolved, before, after, events);
            }

            events.Add(new("verify", "SLO is still breached; escalating."));
            return new(HealingStatus.Escalated, before, after, events);
        }
        catch (Exception error)
        {
            events.Add(new("verify", $"Remediation failed: {error.Message}; escalating."));
            return new(HealingStatus.Escalated, before, null, events);
        }
    }

    private bool Healthy(ServiceHealth health) =>
        double.IsFinite(health.P99Milliseconds) && double.IsFinite(health.ErrorRate) &&
        health.P99Milliseconds >= 0 && health.ErrorRate >= 0 &&
        health.P99Milliseconds <= policy.MaxP99Milliseconds && health.ErrorRate <= policy.MaxErrorRate;
}
