using ContextAssembly.AgentFramework;
using GraphRAG.AgentFramework;
using MemoryConsolidation.AgentFramework;
using MultiSourceContextFusion.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class ContextAssemblerTests
{
    static Candidate Filler(string source, double relevance, int length = 200) =>
        new(source, new string('x', length), relevance);

    [Fact]
    public void TheBudgetIsNeverExceededByUnpinnedItems()
    {
        var context = ContextAssembler.Assemble(
            [Filler("a", 0.9), Filler("b", 0.8), Filler("c", 0.7)], tokenBudget: 60);

        Assert.True(context.Tokens <= 60);
        Assert.NotEmpty(context.Dropped);
    }

    [Fact]
    public void PinnedItemsSurviveEvenWhenTheyBlowTheBudget()
    {
        var context = ContextAssembler.Assemble(
        [
            new("system", new string('x', 400), 1.0, Pinned: true),
            new("user", "the question", 1.0, Pinned: true),
            Filler("retrieval", 0.9)
        ], tokenBudget: 10);

        Assert.Equal(["system", "user"], context.Included.Select(c => c.Source));
    }

    [Fact]
    public void HigherRelevanceWinsTheRemainingBudget()
    {
        var context = ContextAssembler.Assemble(
            [Filler("low", 0.2), Filler("high", 0.9)], tokenBudget: 60);

        Assert.Equal("high", context.Included.Single().Source);
    }

    [Fact]
    public void NearDuplicatesCollapse()
    {
        var context = ContextAssembler.Assemble(
        [
            new("billing", "Seat count rose from 32 to 42 on 11 March, prorated mid-cycle.", 0.9),
            new("crm", "Seat count rose from 32 to 42 on 11 March, prorated mid-cycle.", 0.8)
        ], tokenBudget: 500);

        Assert.Single(context.Included);
        Assert.Contains("duplicate", context.Dropped.Single().Why);
    }

    [Fact]
    public void EveryDropCarriesAReason() =>
        Assert.All(ContextAssembler.Assemble([Filler("a", 0.9), Filler("b", 0.8)], 30).Dropped,
            d => Assert.False(string.IsNullOrWhiteSpace(d.Why)));

    [Fact]
    public void AssemblyIsDeterministic()
    {
        Candidate[] candidates = [Filler("a", 0.5), Filler("b", 0.5), Filler("c", 0.5)];

        Assert.Equal(
            ContextAssembler.Assemble(candidates, 100).Included.Select(c => c.Source),
            ContextAssembler.Assemble(candidates.Reverse().ToArray(), 100).Included.Select(c => c.Source));
    }
}

public class ContextFusionTests
{
    static readonly DateOnly Today = new(2026, 9, 1);

    [Fact]
    public void TrustBeatsRecency()
    {
        var fused = ContextFusion.Fuse(
        [
            new("address", "Storgata 14", "billing", Trust.SystemOfRecord, Today.AddYears(-1)),
            new("address", "Bygdoy alle 3", "ticket", Trust.UserStated, Today)
        ]).Single();

        Assert.Equal("Storgata 14", fused.Winner.Value);
        Assert.True(fused.WasContested);
    }

    [Fact]
    public void RecencyBreaksTiesWithinATrustTier()
    {
        var fused = ContextFusion.Fuse(
        [
            new("plan", "32 seats", "warehouse", Trust.SystemOfRecord, Today.AddDays(-30)),
            new("plan", "42 seats", "billing", Trust.SystemOfRecord, Today.AddDays(-2))
        ]).Single();

        Assert.Equal("42 seats", fused.Winner.Value);
    }

    [Fact]
    public void AgreementIsNotAConflict() =>
        Assert.False(ContextFusion.Fuse(
        [
            new("lang", "Norwegian", "profile", Trust.UserStated, Today),
            new("lang", "Norwegian", "crm", Trust.SystemOfRecord, Today)
        ]).Single().WasContested);

    [Fact]
    public void TheLosingValueIsKeptForTheAudit() =>
        Assert.Equal("Bygdoy alle 3", ContextFusion.Fuse(
        [
            new("address", "Storgata 14", "billing", Trust.SystemOfRecord, Today),
            new("address", "Bygdoy alle 3", "ticket", Trust.UserStated, Today)
        ]).Single().Losers.Single().Value);

    [Fact]
    public void ContestedFieldsAreRenderedAsContested() =>
        Assert.Contains("CONTESTED", ContextFusion.Render(ContextFusion.Fuse(
        [
            new("address", "A", "billing", Trust.SystemOfRecord, Today),
            new("address", "B", "ticket", Trust.UserStated, Today)
        ])));
}

public class KnowledgeGraphTests
{
    static KnowledgeGraph Graph(params Relation[] relations)
    {
        var graph = new KnowledgeGraph();
        foreach (var relation in relations) graph.Add(relation);
        return graph;
    }

    [Fact]
    public void TheSameEdgeFromTwoDocumentsIsOneEdge() =>
        Assert.Single(Graph(
            new Relation("Atlas", "owns", "checkout", "INC-1"),
            new Relation("atlas", "OWNS", "CHECKOUT", "INC-2")).Relations);

    [Fact]
    public void DisconnectedSubjectsFormSeparateCommunities() =>
        Assert.Equal(2, Graph(
            new Relation("Atlas", "owns", "checkout", "INC-1"),
            new Relation("checkout", "depends-on", "payments", "INC-1"),
            new Relation("Delta", "owns", "marketing-site", "INC-5")).Communities().Count);

    [Fact]
    public void OneHopSeesOnlyDirectEdges() =>
        Assert.Single(Graph(
            new Relation("Atlas", "owns", "checkout", "INC-1"),
            new Relation("checkout", "depends-on", "payments", "INC-1")).Neighbourhood("Atlas", hops: 1));

    [Fact]
    public void TwoHopsReachIndirectFacts() =>
        Assert.Equal(2, Graph(
            new Relation("Atlas", "owns", "checkout", "INC-1"),
            new Relation("checkout", "depends-on", "payments", "INC-1")).Neighbourhood("Atlas", hops: 2).Count);

    [Fact]
    public void CommunitiesAreOrderedLargestFirst() =>
        Assert.Equal(2, Graph(
            new Relation("Delta", "owns", "site", "INC-5"),
            new Relation("Atlas", "owns", "checkout", "INC-1"),
            new Relation("checkout", "depends-on", "payments", "INC-1")).Communities()[0].Count);
}

public class EpisodicMemoryTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecentAndRelevantOutranksOldAndImportant()
    {
        var scored = EpisodicRetrieval.Score(
        [
            new("ep-1", "Customer reported export timeouts today.", Now.AddHours(-1), 0.3, "exports"),
            new("ep-2", "Customer payment failed months ago.", Now.AddDays(-60), 0.9, "billing")
        ], "export timeouts", Now);

        Assert.Contains("export", scored[0].Episode.Text);
    }

    [Fact]
    public void RecencyDecaysWithAge()
    {
        var scored = EpisodicRetrieval.Score(
        [
            new("ep-1", "same text here", Now.AddHours(-1), 0.5, "t"),
            new("ep-2", "same text here", Now.AddDays(-30), 0.5, "t")
        ], "unrelated", Now);

        Assert.True(scored[0].Recency > scored[1].Recency);
    }

    [Fact]
    public void OnlyTopicsOverTheThresholdConsolidate()
    {
        Episode[] episodes =
        [
            new("a", "a", Now, 0.5, "exports"), new("b", "b", Now, 0.5, "exports"),
            new("c", "c", Now, 0.5, "exports"),
            new("d", "d", Now, 0.5, "billing"), new("e", "e", Now, 0.5, "billing")
        ];

        Assert.Equal(["exports"], Consolidation.Ripe(episodes, minimum: 3).Select(g => g.Key));
    }

    [Fact]
    public void NothingConsolidatesBelowTheThreshold() =>
        Assert.Empty(Consolidation.Ripe([new("a", "a", Now, 0.5, "exports")], minimum: 3));
}
