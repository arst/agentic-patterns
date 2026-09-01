using AgentRegistry.AgentFramework;
using ControlPlaneAsTool.AgentFramework;
using EventDrivenAgents.AgentFramework;
using SpeculativeToolExecution.AgentFramework;
using StateMachineAgent.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class ExpenseMachineTests
{
    [Fact]
    public void ExecuteIsUnreachableFromClassifyWithoutPlanning() =>
        Assert.DoesNotContain(State.Execute,
            ExpenseMachine.Allowed(State.Classify).Select(d => ExpenseMachine.Next(State.Classify, d)));

    [Fact]
    public void ANonRoutineClaimMustPassThroughApproval() =>
        Assert.Equal(State.Approval, ExpenseMachine.Next(State.Classify, Decision.NeedsApproval));

    [Fact]
    public void AnOffMenuDecisionThrowsRatherThanGuessing() =>
        Assert.Throws<IllegalTransitionException>(() => ExpenseMachine.Next(State.Classify, Decision.Approve));

    [Fact]
    public void TerminalStatesOfferNoDecisions()
    {
        Assert.True(ExpenseMachine.IsTerminal(State.Complete));
        Assert.True(ExpenseMachine.IsTerminal(State.Rejected));
        Assert.Empty(ExpenseMachine.Allowed(State.Complete));
    }

    [Fact]
    public void EveryStateReachableFromIntakeIsTerminalOrHasAWayOut()
    {
        var reachable = new HashSet<State> { State.Intake };
        var queue = new Queue<State>([State.Intake]);

        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            foreach (var decision in ExpenseMachine.Allowed(state))
            {
                var next = ExpenseMachine.Next(state, decision);
                if (reachable.Add(next)) queue.Enqueue(next);
            }
        }

        Assert.Contains(State.Complete, reachable);
        Assert.All(reachable,
            s => Assert.True(ExpenseMachine.IsTerminal(s) || ExpenseMachine.Allowed(s).Count > 0));
    }

    [Fact]
    public void TheVisitBudgetBoundsTheVerifyPlanLoop()
    {
        var budget = new VisitBudget(perState: 2);

        Assert.True(budget.TryVisit(State.Plan));
        Assert.True(budget.TryVisit(State.Plan));
        Assert.False(budget.TryVisit(State.Plan));
    }
}

public class EventBusTests
{
    static AgentEvent Event(string topic, int generation = 0) => new(topic, "payload", "test", generation);

    [Fact]
    public async Task AReactionChainRunsToCompletion()
    {
        var bus = new EventBus(maxEvents: 10, maxGeneration: 5);
        var seen = new List<string>();

        bus.Subscribe("a", e => Task.FromResult<IReadOnlyList<AgentEvent>>([Event("b")]));
        bus.Subscribe("b", e => Task.FromResult<IReadOnlyList<AgentEvent>>([]));

        bus.Publish(Event("a"));
        await bus.RunToCompletionAsync(e => seen.Add(e.Topic));

        Assert.Equal(["a", "b"], seen);
    }

    [Fact]
    public async Task TwoHandlersFeedingEachOtherAreStoppedByTheGenerationCap()
    {
        var bus = new EventBus(maxEvents: 100, maxGeneration: 3);

        bus.Subscribe("ping", e => Task.FromResult<IReadOnlyList<AgentEvent>>([Event("pong")]));
        bus.Subscribe("pong", e => Task.FromResult<IReadOnlyList<AgentEvent>>([Event("ping")]));

        bus.Publish(Event("ping"));
        await bus.RunToCompletionAsync();

        Assert.Equal(4, bus.Published); // generations 0..3
        Assert.NotEmpty(bus.DeadLetters);
    }

    [Fact]
    public void AnEventNobodySubscribesToIsDeadLetteredNotDropped()
    {
        var bus = new EventBus(maxEvents: 10, maxGeneration: 5);

        Assert.False(bus.Publish(Event("nobody-listens")));
        Assert.Single(bus.DeadLetters);
    }

    [Fact]
    public async Task TheEventBudgetIsHard()
    {
        var bus = new EventBus(maxEvents: 2, maxGeneration: 99);
        bus.Subscribe("loop", e => Task.FromResult<IReadOnlyList<AgentEvent>>([Event("loop")]));

        bus.Publish(Event("loop"));
        await bus.RunToCompletionAsync();

        Assert.Equal(2, bus.Published);
    }
}

public class ControlPlaneTests
{
    static ControlPlane Plane(params string[] granted) => new(
    [
        new Backend("search", "Confluence", ["query"], r => $"found {r["query"]}"),
        new Backend("payroll", "SAP", ["employeeId"], r => "salary")
    ], granted.ToHashSet(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void AGrantedCapabilityRoutesToItsBackend() =>
        Assert.Equal("Confluence", Plane("search").Execute("search", """{"query":"vpn"}""").Backend);

    [Fact]
    public void AnUngrantedCapabilityIsRefused() =>
        Assert.False(Plane("search").Execute("payroll", """{"employeeId":"1"}""").Ok);

    [Fact]
    public void AnUnknownCapabilityIsRefused() =>
        Assert.False(Plane("search").Execute("delete_everything", "{}").Ok);

    [Fact]
    public void TheVocabularyLeaksNeitherBackendsNorUngrantedCapabilities()
    {
        var plane = Plane("search");

        Assert.Equal(["search"], plane.Vocabulary);
        Assert.DoesNotContain("SAP", plane.Execute("payroll", "{}").Payload);
    }

    [Fact]
    public void AMissingRequiredFieldIsRefusedBeforeTheBackendRuns() =>
        Assert.False(Plane("search").Execute("search", "{}").Ok);

    [Fact]
    public void MalformedJsonIsRefusedRatherThanThrowing() =>
        Assert.False(Plane("search").Execute("search", "not json").Ok);

    [Fact]
    public void EveryAttemptIsAudited()
    {
        var plane = Plane("search");
        plane.Execute("search", """{"query":"x"}""");
        plane.Execute("payroll", "{}");

        Assert.Equal(2, plane.AuditLog.Count);
        Assert.Contains(plane.AuditLog, l => l.Contains("DENIED"));
    }
}

public class AgentRegistryTests
{
    static readonly byte[] Key = [.. Enumerable.Repeat((byte)7, 32)];
    static readonly DateTimeOffset Now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    static AgentCard Card(string name, DateTimeOffset expires) =>
        new(name, "https://agents.internal/x", ["translate"], expires);

    [Fact]
    public void APublishedCardVerifies()
    {
        var registry = new Registry(Key);
        var published = registry.Publish(Card("peer", Now.AddDays(1)));

        Assert.True(registry.Verify(published, Now).Found);
    }

    [Fact]
    public void TamperingWithTheEndpointBreaksTheSignature()
    {
        var registry = new Registry(Key);
        var published = registry.Publish(Card("peer", Now.AddDays(1)));

        Assert.False(registry.Verify(published with { Endpoint = "https://evil.example" }, Now).Found);
    }

    [Fact]
    public void AddingACapabilityBreaksTheSignature()
    {
        var registry = new Registry(Key);
        var published = registry.Publish(Card("peer", Now.AddDays(1)));

        Assert.False(registry.Verify(published with { Capabilities = ["translate", "wire-transfer"] }, Now).Found);
    }

    [Fact]
    public void AnExpiredCardIsRejectedEvenThoughItVerifies()
    {
        var registry = new Registry(Key);
        var published = registry.Publish(Card("peer", Now.AddDays(-1)));

        Assert.Contains("expired", registry.Verify(published, Now).RejectedBecause);
    }

    [Fact]
    public void AMalformedSignatureIsARejectionNotAnException() =>
        Assert.False(new Registry(Key).Verify(Card("peer", Now.AddDays(1)) with { Signature = "!!!" }, Now).Found);

    [Fact]
    public void DiscoveryReturnsTheForgedCardAsRejectedRatherThanHidingIt()
    {
        var registry = new Registry(Key);
        registry.Publish(Card("good", Now.AddDays(1)));
        registry.PublishRaw(Card("forged", Now.AddDays(1)) with { Signature = "AAAA" });

        var results = registry.Discover("translate", Now);

        Assert.Equal(2, results.Count);
        Assert.Single(results, r => r.Found);
    }

    [Fact]
    public void DiscoveryIgnoresAgentsWithoutTheCapability()
    {
        var registry = new Registry(Key);
        registry.Publish(Card("peer", Now.AddDays(1)));

        Assert.Empty(registry.Discover("wire-transfer", Now));
    }
}

public class SpeculationTests
{
    static readonly Dictionary<string, SpeculatableTool> Policy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = new("read", ReadOnly: true, FreeToDiscard: true),
        ["metered"] = new("metered", ReadOnly: true, FreeToDiscard: false),
        ["write"] = new("write", ReadOnly: false, FreeToDiscard: false)
    };

    [Fact]
    public void OnlyReadOnlyAndFreeToDiscardToolsMaySpeculate()
    {
        var speculator = new Speculator(Policy);

        Assert.True(speculator.Speculate("read", "k1", () => Task.FromResult("v")));
        Assert.False(speculator.Speculate("metered", "k2", () => Task.FromResult("v")));
        Assert.False(speculator.Speculate("write", "k3", () => Task.FromResult("v")));
    }

    [Fact]
    public async Task ARefusedSpeculationNeverRunsTheCall()
    {
        var ran = false;
        var speculator = new Speculator(Policy);

        speculator.Speculate("write", "k", () =>
        {
            ran = true;
            return Task.FromResult("v");
        });

        Assert.False(ran);
        Assert.Equal(0, await speculator.DrainAsync());
    }

    [Fact]
    public async Task AHitServesTheSpeculatedValueWithoutCallingAgain()
    {
        var calls = 0;
        var speculator = new Speculator(Policy);
        Task<string> Call() => Task.FromResult((++calls).ToString());

        speculator.Speculate("read", "k", Call);
        var result = await speculator.ResolveAsync("k", Call);

        Assert.Equal("1", result);
        Assert.Equal(1, calls);
        Assert.True(speculator.Outcomes.Single().Hit);
    }

    [Fact]
    public async Task AMissRunsOnDemandAndIsRecorded()
    {
        var speculator = new Speculator(Policy);

        Assert.Equal("fresh", await speculator.ResolveAsync("never-speculated", () => Task.FromResult("fresh")));
        Assert.False(speculator.Outcomes.Single().Hit);
    }

    [Fact]
    public async Task UnclaimedSpeculationsAreCountedAsWaste()
    {
        var speculator = new Speculator(Policy);
        speculator.Speculate("read", "unused", () => Task.FromResult("v"));

        Assert.Equal(1, await speculator.DrainAsync());
    }
}
