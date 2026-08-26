using ReasoningAndActing;
using Xunit;

namespace AgenticPatterns.Tests;

public class ToolCallBudgetFilterTests
{
    [Fact]
    public async Task TenthCallIsAllowed_EleventhThrows()
    {
        var filter = new ToolCallBudgetFilter();
        var calls = 0;

        for (var i = 0; i < ToolCallBudgetFilter.MaxToolCalls; i++)
            await filter.GuardAsync(() =>
            {
                calls++;
                return Task.CompletedTask;
            });

        Assert.Equal(ToolCallBudgetFilter.MaxToolCalls, calls);

        await Assert.ThrowsAsync<ToolCallBudgetExceededException>(() =>
            filter.GuardAsync(() =>
            {
                calls++;
                return Task.CompletedTask;
            }));

        // The 11th call must not have reached the inner delegate.
        Assert.Equal(ToolCallBudgetFilter.MaxToolCalls, calls);
    }

    [Fact]
    public async Task OverBudgetCall_DoesNotInvokeInnerDelegate()
    {
        var filter = new ToolCallBudgetFilter();
        for (var i = 0; i < ToolCallBudgetFilter.MaxToolCalls; i++)
            await filter.GuardAsync(() => Task.CompletedTask);

        var innerInvoked = false;

        await Assert.ThrowsAsync<ToolCallBudgetExceededException>(() =>
            filter.GuardAsync(() =>
            {
                innerInvoked = true;
                return Task.CompletedTask;
            }));

        Assert.False(innerInvoked);
    }

    [Fact]
    public async Task ExceptionMessage_NamesTheBudget()
    {
        var filter = new ToolCallBudgetFilter();
        for (var i = 0; i < ToolCallBudgetFilter.MaxToolCalls; i++)
            await filter.GuardAsync(() => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<ToolCallBudgetExceededException>(() =>
            filter.GuardAsync(() => Task.CompletedTask));

        Assert.Contains(ToolCallBudgetFilter.MaxToolCalls.ToString(), ex.Message);
    }
}
