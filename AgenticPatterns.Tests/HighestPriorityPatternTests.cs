using AuthenticatedDelegation.AgentFramework;
using ComputerUse.AgentFramework;
using ProgressiveAgentRollout.AgentFramework;
using ReversibleActionCompensation.AgentFramework;
using SelfHealingOperationsLoop.AgentFramework;
using SyntheticUserSimulation.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class ReversibleActionCompensationTests
{
    [Fact]
    public void FailureCompensatesCompletedStepsInReverseOrder()
    {
        var effects = new List<string>();
        CompensableStep Step(string name) => new(name, _ => effects.Add($"apply-{name}"), _ => effects.Add($"undo-{name}"));
        CompensableStep[] steps = [Step("a"), Step("b"), new("c", _ => throw new InvalidOperationException("failed"), _ => { })];

        var result = new SagaRunner().Run("saga-1", steps);

        Assert.Equal(SagaStatus.Compensated, result.Status);
        Assert.Equal(["apply-a", "apply-b", "undo-b", "undo-a"], effects);
    }

    [Fact]
    public void ReplayingASagaDoesNotRepeatEffects()
    {
        var calls = 0;
        CompensableStep[] steps = [new("once", _ => calls++, _ => calls--)];
        var runner = new SagaRunner();

        var first = runner.Run("same-id", steps);
        var replay = runner.Run("same-id", steps);

        Assert.Same(first, replay);
        Assert.Equal(1, calls);
    }
}

public class ProgressiveAgentRolloutTests
{
    [Fact]
    public void ShadowRunsTheCandidateWithoutServingIt()
    {
        var route = new RolloutController(new(2, 0.1, 0.1)).Route("request");

        Assert.True(route.RunCandidate);
        Assert.False(route.ServeCandidate);
    }

    [Fact]
    public void HealthyWindowsPromoteAndARegressionRollsBack()
    {
        var rollout = new RolloutController(new(2, 0.05, 0.1));
        for (var stage = 0; stage < 3; stage++)
        {
            rollout.Observe(new(0.8, 0.9));
            rollout.Observe(new(0.8, 0.9));
        }

        Assert.Equal(RolloutStage.Full, rollout.Stage);

        rollout.Observe(new(0.8, 0.4, true));
        rollout.Observe(new(0.8, 0.4));

        Assert.Equal(RolloutStage.RolledBack, rollout.Stage);
        Assert.Equal(new(false, false), rollout.Route("request"));
    }

    [Fact]
    public void NonFiniteMetricsAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RolloutController(new(2, 0.05, 0.1)).Observe(new(double.NaN, 0.8)));
}

public class SyntheticUserSimulationTests
{
    [Fact]
    public async Task SimulatorReactsToThePriorTurnAndCanStop()
    {
        var observedHistory = -1;
        var result = await new SimulationHarness().RunAsync(
            new("customer", "get help", "impatient"),
            (history, _) =>
            {
                observedHistory = history.Count;
                return Task.FromResult(history.Count == 0 ? new UserMove("help") : new UserMove("done", Stop: true));
            },
            (message, _) => Task.FromResult($"answer to {message}"),
            maxTurns: 3);

        Assert.Single(result.Turns);
        Assert.Equal(1, observedHistory);
        Assert.False(result.ReachedTurnLimit);
    }

    [Fact]
    public async Task HostTurnLimitStopsAnEndlessPersona()
    {
        var result = await new SimulationHarness().RunAsync(
            new("customer", "keep talking", "persistent"),
            (_, _) => Task.FromResult(new UserMove("again")),
            (_, _) => Task.FromResult("reply"),
            maxTurns: 2);

        Assert.True(result.ReachedTurnLimit);
        Assert.Equal(2, result.Turns.Count);
    }
}

public class ComputerUseTests
{
    [Fact]
    public void HiddenControlsCannotBeClickedThroughAPopup()
    {
        var desktop = new VirtualDesktop();

        var result = desktop.Apply(new("click", 10, 5));

        Assert.False(result.Applied);
        Assert.True(desktop.PopupOpen);
    }

    [Fact]
    public async Task ScreenshotActionObservationLoopReachesTheGoal()
    {
        var result = await new ComputerUseRunner().RunAsync(
            new VirtualDesktop(),
            (screen, _) =>
            {
                var target = screen.Elements[0];
                return Task.FromResult(new GuiAction("click", target.X, target.Y));
            },
            screen => screen.DarkMode,
            maxSteps: 3);

        Assert.True(result.Completed);
        Assert.Equal(3, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.True(step.Applied));
    }
}

public class AuthenticatedDelegationTests
{
    private static readonly byte[] Key = [.. Enumerable.Repeat((byte)7, 32)];
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static (DelegatedResourceServer Server, DelegationGrant Grant) Setup()
    {
        var authority = new DelegationAuthority(Key);
        var grant = authority.Issue(new("g1", "user", "agent", "payments", ["pay"], "invoice-1", 100m,
            Now.AddMinutes(-1), Now.AddMinutes(5)));
        return (new(authority, "payments"), grant);
    }

    [Fact]
    public void ScopedRequestIsAuthorizedAndAttributed()
    {
        var (server, grant) = Setup();
        var decision = server.Authorize(new("r1", "agent", "payments", "pay", "invoice-1", 75m, grant), Now);

        Assert.True(decision.Allowed);
        Assert.Collection(server.AuditLog, entry =>
        {
            Assert.Equal("user", entry.User);
            Assert.Equal("agent", entry.Agent);
            Assert.True(entry.Allowed);
        });
    }

    [Fact]
    public void TamperingAndExcessAuthorityAreRejected()
    {
        var (server, grant) = Setup();

        Assert.False(server.Authorize(new("r1", "agent", "payments", "pay", "invoice-2", 75m, grant), Now).Allowed);
        Assert.False(server.Authorize(new("r2", "agent", "payments", "pay", "invoice-1", 101m, grant), Now).Allowed);
        Assert.False(server.Authorize(new("r3", "agent", "payments", "pay", "invoice-1", 75m,
            grant with { MaxAmount = 1000m }), Now).Allowed);
        Assert.Equal(3, server.AuditLog.Count);
    }
}

public class SelfHealingOperationsLoopTests
{
    private static readonly HealingPolicy Policy = new(
        450, 0.02, new HashSet<string>(StringComparer.Ordinal) { "rollback" }, 0.8);
    private static readonly ServiceHealth Unhealthy = new("v2", 1900, 0.14, "after v2 deploy");

    [Fact]
    public void OutOfPolicyRemediationEscalatesWithoutExecuting()
    {
        var executed = false;
        var result = new SelfHealingLoop(Policy).Run(Unhealthy, new("run_migration", 0.99, "guess"), _ =>
        {
            executed = true;
            return Unhealthy;
        });

        Assert.Equal(HealingStatus.Escalated, result.Status);
        Assert.False(executed);
    }

    [Fact]
    public void ApprovedRemediationMustVerifyRecovery()
    {
        var loop = new SelfHealingLoop(Policy);

        var recovered = loop.Run(Unhealthy, new("rollback", 0.95, "bad deploy"),
            _ => new("v1", 300, 0.005, "baseline"));
        var stillBroken = loop.Run(Unhealthy, new("rollback", 0.95, "bad deploy"), _ => Unhealthy);

        Assert.Equal(HealingStatus.Resolved, recovered.Status);
        Assert.Equal(HealingStatus.Escalated, stillBroken.Status);
    }

    [Fact]
    public void NonFiniteConfidenceCannotAuthorizeAnAction()
    {
        var executed = false;
        var result = new SelfHealingLoop(Policy).Run(Unhealthy, new("rollback", double.NaN, "invalid"), _ =>
        {
            executed = true;
            return Unhealthy;
        });

        Assert.Equal(HealingStatus.Escalated, result.Status);
        Assert.False(executed);
    }
}
