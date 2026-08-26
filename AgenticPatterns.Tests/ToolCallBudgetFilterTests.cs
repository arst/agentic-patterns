using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ReasoningAndActing;
using Xunit;

namespace AgenticPatterns.Tests;

public class ToolCallBudgetFilterTests
{
    // A real AutoFunctionInvocationContext — the exact type SK 1.79 hands the filter at runtime.
    // Its 5-argument constructor is public, so no test seam is needed: these tests drive
    // OnAutoFunctionInvocationAsync itself, which is the only method the runtime ever calls.
    private static AutoFunctionInvocationContext NewContext()
    {
        var kernel = new Kernel();
        var function = KernelFunctionFactory.CreateFromMethod(() => "tool ran", "Tool");
        return new AutoFunctionInvocationContext(
            kernel, function, new FunctionResult(function), new ChatHistory(),
            new ChatMessageContent(AuthorRole.Assistant, "calling a tool"));
    }

    private static async Task<(bool InnerRan, AutoFunctionInvocationContext Context)> InvokeAsync(
        ToolCallBudgetFilter filter)
    {
        var context = NewContext();
        var innerRan = false;
        await filter.OnAutoFunctionInvocationAsync(context, _ =>
        {
            innerRan = true;
            return Task.CompletedTask;
        });
        return (innerRan, context);
    }

    [Fact]
    public async Task TenthCallRuns_EleventhIsRefused()
    {
        var filter = new ToolCallBudgetFilter();

        for (var i = 0; i < ToolCallBudgetFilter.MaxToolCalls; i++)
        {
            var (ran, ctx) = await InvokeAsync(filter);
            Assert.True(ran);
            Assert.False(ctx.Terminate);
            Assert.False(filter.BudgetExhausted);
        }

        var (eleventhRan, eleventhCtx) = await InvokeAsync(filter);

        // The 11th tool body must not run...
        Assert.False(eleventhRan);
        // ...and the auto-invocation loop must stop, rather than the model being handed an error
        // to paraphrase and carrying on. Terminate is the only stop SK 1.79 honours.
        Assert.True(eleventhCtx.Terminate);
        Assert.True(filter.BudgetExhausted);
    }

    [Fact]
    public async Task OverBudgetCall_DoesNotThrow()
    {
        // Throwing is what the runtime swallows: SK converts the exception into a tool-result
        // error message and keeps looping. The filter must return normally instead.
        var filter = new ToolCallBudgetFilter();
        for (var i = 0; i < ToolCallBudgetFilter.MaxToolCalls; i++)
            await InvokeAsync(filter);

        var exception = await Record.ExceptionAsync(() => InvokeAsync(filter));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopReason_NamesTheBudget()
    {
        var filter = new ToolCallBudgetFilter();
        for (var i = 0; i <= ToolCallBudgetFilter.MaxToolCalls; i++)
            await InvokeAsync(filter);

        Assert.True(filter.BudgetExhausted);
        Assert.Contains(ToolCallBudgetFilter.MaxToolCalls.ToString(), filter.StopReason);
    }

    [Fact]
    public async Task BudgetIsPerInstance_NotPerProcess()
    {
        // Program.cs registers one instance per run; a second instance is a second run and must
        // start with a full budget.
        var exhausted = new ToolCallBudgetFilter();
        for (var i = 0; i <= ToolCallBudgetFilter.MaxToolCalls; i++)
            await InvokeAsync(exhausted);
        Assert.True(exhausted.BudgetExhausted);

        var (ran, ctx) = await InvokeAsync(new ToolCallBudgetFilter());

        Assert.True(ran);
        Assert.False(ctx.Terminate);
    }
}
