using ProgressiveAgentRollout.AgentFramework;

var rollout = new RolloutController(new(
    MinimumSamples: 3,
    MaxScoreRegression: 0.05,
    MaxFailureRate: 0.1));

Console.WriteLine($"Start: {rollout.Stage}; candidate runs in shadow, but users get control output.");
Console.WriteLine($"Shadow route: {rollout.Route("request-1")}");

for (var stage = 0; stage < 3; stage++)
{
    for (var sample = 0; sample < 3; sample++)
        rollout.Observe(new(ControlScore: 0.82, CandidateScore: 0.86));

    Console.WriteLine($"Healthy evaluation window -> {rollout.Stage}");
}

rollout.Observe(new(0.84, 0.55, CandidateFailed: true));
rollout.Observe(new(0.82, 0.58));
rollout.Observe(new(0.85, 0.54));

Console.WriteLine($"Regressed production window -> {rollout.Stage}");
Console.WriteLine($"After rollback: {rollout.Route("request-1")}");
