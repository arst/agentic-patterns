using ChainOfVerification.AgentFramework;
using DualLlm.AgentFramework;
using EventDrivenAgents.AgentFramework;
using GraphOfThoughts.AgentFramework;
using LeastToMost.AgentFramework;
using MemoryConsolidation.AgentFramework;
using MemoryPoisoningPrevention.AgentFramework;
using ProactiveClarification.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class ClarificationMergeTests
{
    static readonly HashSet<string> Known =
        new(["destination", "checkIn", "nights", "budget"], StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, string> Filled(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AnAnsweredSlotIsWrittenBackIntoState()
    {
        var filled = Filled();

        ClarificationGate.Merge(filled, Known, new HashSet<string>(["destination"]),
            [("destination", "Berlin")]);

        Assert.Equal("Berlin", filled["destination"]);
    }

    [Fact]
    public void AVolunteeredSlotNobodyAskedAboutIsStillKept()
    {
        // The user answering more than was asked is information, not an attack - and discarding
        // it only to invent a default is the failure the pattern exists to avoid.
        var filled = Filled();

        var merged = ClarificationGate.Merge(filled, Known, new HashSet<string>(["destination"]),
            [("budget", "max EUR 150")]);

        Assert.True(merged.Single().Merged);
        Assert.Equal("max EUR 150", filled["budget"]);
    }

    [Fact]
    public void AReplyCannotSilentlyRewriteASlotTheRequestAlreadySettled()
    {
        var filled = Filled(("destination", "Oslo"));

        var merged = ClarificationGate.Merge(filled, Known, new HashSet<string>(["nights"]),
            [("destination", "Berlin")]);

        Assert.False(merged.Single().Merged);
        Assert.Equal("Oslo", filled["destination"]);
    }

    [Fact]
    public void ASettledSlotMayBeChangedWhenAQuestionAskedAboutIt()
    {
        var filled = Filled(("nights", "2"));

        ClarificationGate.Merge(filled, Known, new HashSet<string>(["nights"]), [("nights", "3")]);

        Assert.Equal("3", filled["nights"]);
    }

    [Fact]
    public void UnknownSlotsAndEmptyValuesAreIgnored()
    {
        var filled = Filled();

        var merged = ClarificationGate.Merge(filled, Known, new HashSet<string>(["destination", "nights"]),
            [("airline", "SAS"), ("nights", "  ")]);

        Assert.All(merged, m => Assert.False(m.Merged));
        Assert.Empty(filled);
    }
}

public class DualLlmValuePolicyTests
{
    static Value Tainted(string content) => new("v", "decimal", content, Tainted: true);

    [Fact]
    public void TheInjectedAmountIsAPerfectlyValidDecimal() =>
        // The point of the whole test class: type safety had nothing to say about this value.
        Assert.True(DataFlowPlan.TryCoerce(Tainted("48000.00"), "decimal", out _));

    [Fact]
    public void AndTheValuePolicyIsWhatStopsIt() =>
        Assert.NotNull(DataFlowPlan.UnattendedViolation(Tainted("48000.00"), 10_000m));

    [Fact]
    public void AnAmountUnderTheLimitPassesUnattended() =>
        Assert.Null(DataFlowPlan.UnattendedViolation(Tainted("4182.50"), 10_000m));

    [Fact]
    public void AnUntaintedValueIsNotSubjectToTheUnattendedLimit() =>
        Assert.Null(DataFlowPlan.UnattendedViolation(
            new Value("v", "decimal", "48000.00", Tainted: false), 10_000m));
}

public class EventBusTaxonomyTests
{
    static AgentEvent Event(string topic, int generation = 0) => new(topic, "payload", "test", generation);

    [Fact]
    public void ADeclaredTerminalTopicIsAnOutcomeNotADeadLetter()
    {
        var bus = new EventBus(maxEvents: 10, maxGeneration: 5);
        bus.RegisterTerminal("workflow-finished");

        bus.Publish(Event("workflow-finished"));

        Assert.Single(bus.TerminalEvents);
        Assert.Empty(bus.DeadLetters);
    }

    [Fact]
    public void AnUndeclaredTopicIsADeliveryFailure()
    {
        var bus = new EventBus(maxEvents: 10, maxGeneration: 5);

        // The typo case: neither subscribed nor registered as terminal. Inferring "terminal" from
        // an empty handler list would make this a successful outcome.
        bus.Publish(Event("DecisionMdae"));

        Assert.Empty(bus.TerminalEvents);
        Assert.Equal(Refusal.NoSubscriber, bus.DeadLetters.Single().Reason);
    }

    [Fact]
    public async Task AGenerationCapProducesADeadLetterWithThatReason()
    {
        var bus = new EventBus(maxEvents: 100, maxGeneration: 2);
        bus.Subscribe("ping", _ => Task.FromResult<IReadOnlyList<AgentEvent>>([Event("ping")]));

        bus.Publish(Event("ping"));
        await bus.RunToCompletionAsync();

        Assert.Equal(Refusal.GenerationLimit, bus.DeadLetters.Single().Reason);
    }

    [Fact]
    public async Task TheRunBudgetProducesItsOwnReason()
    {
        var bus = new EventBus(maxEvents: 2, maxGeneration: 99);
        bus.Subscribe("loop", _ => Task.FromResult<IReadOnlyList<AgentEvent>>([Event("loop")]));

        bus.Publish(Event("loop"));
        await bus.RunToCompletionAsync();

        Assert.Equal(Refusal.RunBudgetExceeded, bus.DeadLetters.Single().Reason);
    }
}

public class EvidenceIndependenceTests
{
    static readonly Source Page = new("web:vendor.example/sla", Trust.WebContent);
    static readonly Source SamePageScraped = new("web:vendor.example/sla", Trust.ToolOutput);
    static readonly Source OtherPublisher = new("web:review.example/vendors", Trust.WebContent);
    static readonly Source Contract = new("system:contracts/778", Trust.ToolOutput);

    static List<MemoryItem> StoreWith(Source source) =>
        [new("sla", "4", source)];

    [Fact]
    public void TheSameEvidenceFetchedByADifferentMechanismIsNotCorroboration() =>
        // The failure the old trust-class test had: a scraper reading the page it was seeded from
        // counted as a second opinion.
        Assert.Equal(Tier.Quarantined,
            MemoryGate.Admit(new MemoryItem("sla", "4", SamePageScraped), StoreWith(Page)).Item.Tier);

    [Fact]
    public void TwoUnrelatedPublishersOfTheSameTrustClassDoCorroborate() =>
        // The other direction, which the old test could not express at all.
        Assert.Equal(Tier.Active,
            MemoryGate.Admit(new MemoryItem("sla", "4", OtherPublisher), StoreWith(Page)).Item.Tier);

    [Fact]
    public void AGenuinelyIndependentSystemCorroborates() =>
        Assert.Equal(Tier.Active,
            MemoryGate.Admit(new MemoryItem("sla", "4", Contract), StoreWith(Page)).Item.Tier);

    [Fact]
    public void AnAuthoritativeFactStillCannotBeOverwritten() =>
        Assert.Equal(Tier.Rejected, MemoryGate.Admit(
            new MemoryItem("limit", "50000", new Source("web:evil.example", Trust.WebContent)),
            [new MemoryItem("limit", "250", new Source("system:billing", Trust.Authoritative), Tier.Active)])
            .Item.Tier);
}

public class ConsolidationProvenanceTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    static Episode Ep(string id, string topic, EpisodeStatus status = EpisodeStatus.Active) =>
        new(id, "text about exports", Now, 0.5, topic, status);

    [Fact]
    public void ASemanticMemoryNamesTheEpisodesItCameFrom()
    {
        var memory = new SemanticMemory("exports are slow at month-end", "exports", ["ep-01", "ep-02"], Now);

        Assert.Equal(2, memory.ConsolidatedFrom);
        Assert.Equal(["ep-01", "ep-02"], memory.SourceEpisodeIds);
    }

    [Fact]
    public void ArchivedEpisodesLeaveTheHotRetrievalSet() =>
        Assert.Empty(EpisodicRetrieval.Score(
            [Ep("ep-01", "exports", EpisodeStatus.Archived)], "exports", Now));

    [Fact]
    public void ArchivedEpisodesDoNotReConsolidate() =>
        Assert.Empty(Consolidation.Ripe(
            [Ep("a", "exports", EpisodeStatus.Archived), Ep("b", "exports", EpisodeStatus.Archived),
             Ep("c", "exports", EpisodeStatus.Archived)], minimum: 3));

    [Fact]
    public void ActiveEpisodesStillConsolidateNormally() =>
        Assert.Single(Consolidation.Ripe(
            [Ep("a", "exports"), Ep("b", "exports"), Ep("c", "exports")], minimum: 3));
}

public class StepCheckTests
{
    [Fact]
    public void TheBillingScheduleIsComputedFromTheRulesNotHardcoded() =>
        Assert.Equal(144m, StepChecks.BillingTotal(
            new DateOnly(2025, 3, 3), new DateOnly(2025, 7, 3), new DateOnly(2025, 10, 15), 14m, 22m));

    [Fact]
    public void ACorrectTotalPasses() =>
        Assert.True(StepChecks.AgainstTotal("Anna paid EUR 144 in total.", 144m).Passed);

    [Fact]
    public void AWrongTotalFailsAndSaysBothFigures()
    {
        var result = StepChecks.AgainstTotal("Anna paid EUR 166 in total.", 144m);

        Assert.False(result.Passed);
        Assert.Contains("166", result.Detail);
        Assert.Contains("144", result.Detail);
    }

    [Fact]
    public void AnAnswerWithNoTotalFails() =>
        Assert.False(StepChecks.AgainstTotal("It depends on the billing cycle.", 144m).Passed);

    [Fact]
    public void TheConcludingFigureIsTheOneChecked() =>
        // "4 x 14 = 56 ... 4 x 22 = 88 ... total EUR 144" - the last figure is the answer.
        Assert.True(StepChecks.AgainstTotal(
            "Four months at EUR 14 is EUR 56, four at EUR 22 is EUR 88, for EUR 144.", 144m).Passed);
}

public class LengthPolicyTests
{
    static string Sentences(int n) => string.Join(" ", Enumerable.Repeat("A risk exists here.", n));

    [Fact]
    public void ACandidateInsideTheBriefKeepsItsScore() =>
        Assert.Equal(0.95, LengthPolicy.Apply(0.95, Sentences(6), 6).Score);

    [Fact]
    public void AnOverlongCandidateIsCappedByTheHost() =>
        Assert.Equal(0.6, LengthPolicy.Apply(0.95, Sentences(7), 6).Score);

    [Fact]
    public void AMuchTooLongCandidateIsCappedHarder() =>
        Assert.Equal(0.3, LengthPolicy.Apply(0.95, Sentences(12), 6).Score);

    [Fact]
    public void TheCapNeverRaisesAScore() =>
        Assert.Equal(0.2, LengthPolicy.Apply(0.2, Sentences(20), 6).Score);

    [Fact]
    public void ThePenaltyExplainsItself() =>
        Assert.Contains("7 sentences", LengthPolicy.Apply(0.9, Sentences(7), 6).Penalty);
}

public class VerificationGateStillHoldsTests
{
    [Fact]
    public void TheLeakCheckIsUnchangedByTheReframing() =>
        Assert.NotEmpty(VerificationGate.Validate(
            new Claim(1, "Cologne was founded in 38 BC.", "38 BC"), "Was Cologne founded in 38 BC?"));
}
