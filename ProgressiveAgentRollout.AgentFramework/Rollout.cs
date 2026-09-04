using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ProgressiveAgentRollout.AgentFramework;

public enum RolloutStage
{
    Shadow,
    Canary,
    Ramp,
    Full,
    RolledBack
}

public sealed record RolloutPolicy(
    int MinimumSamples,
    double MaxScoreRegression,
    double MaxFailureRate,
    int CanaryPercent = 5,
    int RampPercent = 25);

public sealed record RolloutSample(double ControlScore, double CandidateScore, bool CandidateFailed = false);

public sealed record RouteDecision(bool ServeCandidate, bool RunCandidate);

public sealed class RolloutController
{
    private readonly RolloutPolicy policy;
    private readonly List<RolloutSample> window = [];

    public RolloutController(RolloutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MinimumSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy.MinimumSamples));
        if (!double.IsFinite(policy.MaxScoreRegression) || !double.IsFinite(policy.MaxFailureRate) ||
            policy.MaxScoreRegression < 0 || policy.MaxFailureRate is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.CanaryPercent is < 1 or > 99 || policy.RampPercent <= policy.CanaryPercent || policy.RampPercent > 99)
            throw new ArgumentOutOfRangeException(nameof(policy));

        this.policy = policy;
    }

    public RolloutStage Stage { get; private set; } = RolloutStage.Shadow;

    public RouteDecision Route(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        return Stage switch
        {
            RolloutStage.Shadow => new(false, true),
            RolloutStage.Canary => Selected(requestId, policy.CanaryPercent),
            RolloutStage.Ramp => Selected(requestId, policy.RampPercent),
            RolloutStage.Full => new(true, true),
            _ => new(false, false)
        };
    }

    public RolloutStage Observe(RolloutSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!double.IsFinite(sample.ControlScore) || !double.IsFinite(sample.CandidateScore))
            throw new ArgumentOutOfRangeException(nameof(sample));

        if (Stage == RolloutStage.RolledBack)
            return Stage;

        window.Add(sample);
        if (window.Count < policy.MinimumSamples)
            return Stage;

        var failureRate = window.Count(item => item.CandidateFailed) / (double)window.Count;
        var scoreRegression = window.Average(item => item.ControlScore) - window.Average(item => item.CandidateScore);
        window.Clear();

        if (failureRate > policy.MaxFailureRate || scoreRegression > policy.MaxScoreRegression)
            return Stage = RolloutStage.RolledBack;

        return Stage = Stage switch
        {
            RolloutStage.Shadow => RolloutStage.Canary,
            RolloutStage.Canary => RolloutStage.Ramp,
            RolloutStage.Ramp => RolloutStage.Full,
            _ => Stage
        };
    }

    private static RouteDecision Selected(string requestId, int percentage)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
        var bucket = BinaryPrimitives.ReadUInt32BigEndian(hash) % 100;
        var selected = bucket < percentage;
        return new(selected, selected);
    }
}
