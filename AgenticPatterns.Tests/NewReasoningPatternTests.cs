using ChainOfVerification.AgentFramework;
using GraphOfThoughts.AgentFramework;
using LeastToMost.AgentFramework;
using MixtureOfAgents.AgentFramework;
using ProactiveClarification.AgentFramework;
using StepBack.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class VerificationGateTests
{
    static readonly Claim Founded = new(1, "Cologne was founded in 38 BC.", "38 BC");

    [Fact]
    public void AQuestionCarryingTheDraftedValueIsRejected() =>
        Assert.NotEmpty(VerificationGate.Validate(Founded, "Was Cologne founded in 38 BC?"));

    [Fact]
    public void TheSameValueSplitAcrossTheQuestionStillCounts() =>
        Assert.NotEmpty(VerificationGate.Validate(Founded, "In 38, specifically BC, was Cologne founded?"));

    [Fact]
    public void AnOpenQuestionIsAllowed() =>
        Assert.Empty(VerificationGate.Validate(Founded, "In what year was Cologne founded?"));

    [Fact]
    public void PartialOverlapWithTheValueIsNotALeak() =>
        Assert.Empty(VerificationGate.Validate(Founded, "Which century BC saw Cologne founded?"));

    [Fact]
    public void AnEmptyQuestionIsRejected() =>
        Assert.NotEmpty(VerificationGate.Validate(Founded, "   "));
}

public class ClarificationGateTests
{
    static readonly Slot[] Slots =
    [
        new("destination", ["city", "where"]),
        new("nights", ["nights", "how long"]),
        new("budget", ["budget", "per night"])
    ];

    static IReadOnlyList<ScreenedQuestion> Screen(string[] questions, params string[] filled) =>
        ClarificationGate.Screen(Slots, filled.ToHashSet(StringComparer.OrdinalIgnoreCase), questions, 3);

    [Fact]
    public void AQuestionAboutAFilledSlotIsDropped() =>
        Assert.False(Screen(["Which city?"], "destination").Single().Allowed);

    [Fact]
    public void AQuestionAboutAMissingSlotIsAllowed() =>
        Assert.True(Screen(["Which city?"]).Single().Allowed);

    [Fact]
    public void AQuestionThatTargetsNoSlotIsDropped() =>
        Assert.False(Screen(["Could you tell me more?"]).Single().Allowed);

    [Fact]
    public void TheSameSlotIsNotAskedTwice() =>
        Assert.Single(Screen(["Which city?", "Where are you going?"]), q => q.Allowed);

    [Fact]
    public void TheBudgetCapsHowManySurvive()
    {
        var screened = ClarificationGate.Screen(Slots, new HashSet<string>(),
            ["Which city?", "How long?", "What budget?"], maxQuestions: 2);

        Assert.Equal(2, screened.Count(q => q.Allowed));
    }
}

public class ThoughtGraphTests
{
    [Fact]
    public void AThoughtCannotNameAParentThatDoesNotExistYet()
    {
        var graph = new ThoughtGraph();
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.Add("draft", "x", [7], 0.5));
    }

    [Fact]
    public void AggregationRecordsBothParents()
    {
        var graph = new ThoughtGraph();
        var a = graph.Add("draft", "a", [], 0.4);
        var b = graph.Add("draft", "b", [], 0.6);
        var merged = graph.Add("aggregate", "ab", [a, b], 0.8);

        Assert.Equal([a, b], graph.Ancestors(merged));
    }

    [Fact]
    public void AncestorsAreTransitive()
    {
        var graph = new ThoughtGraph();
        var a = graph.Add("draft", "a", [], 0.4);
        var b = graph.Add("refine", "b", [a], 0.5);
        var c = graph.Add("refine", "c", [b], 0.6);

        Assert.Equal([a, b], graph.Ancestors(c));
    }

    [Fact]
    public void BestPrefersTheLaterThoughtOnATie()
    {
        var graph = new ThoughtGraph();
        graph.Add("draft", "early", [], 0.7);
        var later = graph.Add("refine", "late", [0], 0.7);

        Assert.Equal(later, graph.Best().Id);
    }
}

public class DecompositionTests
{
    const string Question = "How much did Anna pay in total?";

    [Fact]
    public void TheOriginalQuestionIsAlwaysTheLastStep() =>
        Assert.Equal(Question, Decomposition.Normalize(["How many months at EUR 14?"], Question, 5)[^1].Question);

    [Fact]
    public void ARestatedQuestionIsNotDuplicatedAtTheEnd()
    {
        var steps = Decomposition.Normalize(["How many months?", "how much did anna pay in total"], Question, 5);

        Assert.Equal(2, steps.Count);
        Assert.Equal(Question, steps[^1].Question);
    }

    [Fact]
    public void DuplicatesAndBlanksAreDropped()
    {
        var steps = Decomposition.Normalize(["A", "A", "  ", "B"], Question, 5);

        Assert.Equal(["A", "B", Question], steps.Select(s => s.Question));
    }

    [Fact]
    public void TheCapCountsTheAppendedQuestion() =>
        Assert.Equal(3, Decomposition.Normalize(["A", "B", "C", "D"], Question, max: 3).Count);
}

public class PrincipleGateTests
{
    const string Question = "A 2.0 kg block slides 5.0 m down a 30 degree ramp. What is its speed?";

    [Fact]
    public void APrincipleRepeatingTheQuestionsNumbersIsFlagged() =>
        Assert.NotEmpty(PrincipleGate.LeakedSpecifics(Question,
            "Energy is conserved, so a 2.0 kg block converts mgh into kinetic energy."));

    [Fact]
    public void AnAbstractPrincipleIsClean() =>
        Assert.Empty(PrincipleGate.LeakedSpecifics(Question,
            "On a frictionless incline, gravitational potential energy converts entirely to kinetic energy."));

    [Fact]
    public void AQuestionWithoutNumbersCannotLeak() =>
        Assert.Empty(PrincipleGate.LeakedSpecifics("Why do objects fall?", "Gravity acts at 9.81 m/s squared."));
}

public class ProposalSetTests
{
    static readonly Proposal[] Three =
        [new("A", "alpha"), new("B", "beta"), new("C", "gamma")];

    [Fact]
    public void EveryReaderSeesEveryProposal()
    {
        var set = new ProposalSet(Three);

        for (var reader = 0; reader < set.Count; reader++)
            Assert.Equal(["alpha", "beta", "gamma"], set.For(reader).Select(p => p.Text).Order());
    }

    [Fact]
    public void DifferentReadersSeeDifferentOrderings()
    {
        var set = new ProposalSet(Three);

        Assert.NotEqual(set.For(0).Select(p => p.Text), set.For(1).Select(p => p.Text));
    }

    [Fact]
    public void TheRenderedTextIsAnonymised()
    {
        var formatted = new ProposalSet([new("Optimist", "alpha"), new("Pessimist", "beta")]).Format(0);

        Assert.DoesNotContain("Optimist", formatted);
        Assert.DoesNotContain("Pessimist", formatted);
        Assert.Contains("Proposal A:", formatted);
    }

    [Fact]
    public void EmptyProposalsAreDropped() =>
        Assert.Equal(1, new ProposalSet([new("A", "alpha"), new("B", "   ")]).Count);

    [Fact]
    public void ALayerThatProducedNothingIsAnError() =>
        Assert.Throws<ArgumentException>(() => new ProposalSet([new("A", "")]));
}
