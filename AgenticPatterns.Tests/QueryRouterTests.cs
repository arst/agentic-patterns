using Microsoft.Extensions.AI;
using ResourceAwareOptimization.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class QueryRouterTests
{
    private static List<ChatMessage> User(string text) => [new(ChatRole.User, text)];

    [Theory]
    [InlineData("What is the capital of France?", "simple")]
    [InlineData("Hi, how are you?", "simple")]
    [InlineData("Explain step by step why gradient descent converges.", "reasoning")]
    [InlineData("Please analyze these results.", "reasoning")]
    public void Classify_Cases(string text, string expected)
    {
        Assert.Equal(expected, QueryRouter.Classify(User(text)));
    }

    [Fact]
    public void BudgetExceeded_SimpleQuery_IsNotRefused()
    {
        // The documented behavior: the fast tier stays available after the budget trips
        Assert.False(QueryRouter.RefuseForBudget(User("Hi, how are you?"), budgetExceeded: true));
    }

    [Fact]
    public void BudgetExceeded_ReasoningQuery_IsRefused()
    {
        Assert.True(QueryRouter.RefuseForBudget(User("Analyze this step by step."), budgetExceeded: true));
    }

    [Fact]
    public void BudgetNotExceeded_NothingIsRefused()
    {
        Assert.False(QueryRouter.RefuseForBudget(User("Analyze this step by step."), budgetExceeded: false));
    }
}
