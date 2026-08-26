using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ReasoningAndActing;
using Xunit;

namespace AgenticPatterns.Tests;

/// <summary>
/// <see cref="ToolCallBudgetFilterTests"/> drives <c>OnAutoFunctionInvocationAsync</c> directly —
/// that proves the filter's own logic, but proves nothing about whether Semantic Kernel's real
/// auto-invocation loop actually honours <c>context.Terminate</c>. The first cut of this control
/// was an <c>IFunctionInvocationFilter</c> that threw; that passed its own unit tests but, run
/// against real SK 1.79, made 129 model calls instead of 11 because
/// <c>FunctionCallsProcessor.ExecuteFunctionCallAsync</c> swallows the exception into a tool-result
/// error and keeps looping. These tests close that gap: they build a real kernel, wire
/// <see cref="ToolCallBudgetFilter"/> in the same shape as
/// <c>ReasoningAndActing/Program.cs</c>, and drive <c>IChatCompletionService</c> with
/// <c>FunctionChoiceBehavior.Auto()</c> against a stub HTTP handler
/// (<see cref="ScriptedToolCallHttpHandler"/>) that answers every completion request with another
/// tool call, forever. The loop only stops if <c>Terminate</c> actually stops it.
/// </summary>
public class ToolCallBudgetFilterRealLoopTests
{
    /// <summary>The one tool the stub loop calls; counts how many times its body actually ran.</summary>
    private sealed class CountingTool
    {
        public int Calls { get; private set; }

        [KernelFunction]
        [Description("A tool that can be called any number of times.")]
        public string Invoke()
        {
            Calls++;
            return "ok";
        }
    }

    private static (Kernel Kernel, ToolCallBudgetFilter Filter, CountingTool Tool, ScriptedToolCallHttpHandler Handler)
        BuildKernel(int toolCallsPerTurn)
    {
        var handler = new ScriptedToolCallHttpHandler(toolCallsPerTurn);
        var httpClient = new HttpClient(handler);

        var builder = Kernel.CreateBuilder();
        // Same connector Program.cs uses (Connectors.OpenAI, transitively via
        // Connectors.AzureOpenAI); httpClient points every request at the stub handler above
        // instead of a real endpoint — no port, no socket, no Settings/credentials needed.
        builder.AddOpenAIChatCompletion(modelId: "stub-model", apiKey: "stub-key", httpClient: httpClient);

        // Same registration shape as Program.cs: one filter instance per run.
        var filter = new ToolCallBudgetFilter();
        builder.Services.AddSingleton<IAutoFunctionInvocationFilter>(filter);

        var kernel = builder.Build();
        var tool = new CountingTool();
        kernel.Plugins.AddFromObject(tool, "Tool");

        return (kernel, filter, tool, handler);
    }

    private static async Task<ChatMessageContent> RunLoopAsync(Kernel kernel)
    {
        var service = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddUserMessage("Call the tool as many times as you like.");
        var settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        return await service.GetChatMessageContentAsync(history, settings, kernel);
    }

    [Fact]
    public async Task RealAutoInvocationLoop_StopsExactlyAtTheBudget()
    {
        var (kernel, filter, tool, handler) = BuildKernel(toolCallsPerTurn: 1);

        await RunLoopAsync(kernel);

        // Boundary is exact: the 10th tool body runs, the 11th never does.
        Assert.Equal(ToolCallBudgetFilter.MaxToolCalls, tool.Calls);
        Assert.True(filter.BudgetExhausted);

        // Model-call count: one call per round in this single-call-per-turn shape, so it is exactly
        // MaxToolCalls (10 rounds that each run their tool) + 1 (the round whose tool call is
        // refused and terminates the loop before a 12th model call is ever made). Asserted exact,
        // not as a bound: the stub is fully synchronous and deterministic, and the reviewer's own
        // run also landed on exactly 11 — a bound would hide a regression that shaves rounds off.
        Assert.Equal(ToolCallBudgetFilter.MaxToolCalls + 1, handler.RequestCount);

        // The refusal must never be handed to the model as tool output to paraphrase — Terminate
        // ends the loop before any further request is built, so no request body the model saw
        // should mention the budget at all.
        Assert.DoesNotContain(handler.RequestBodies, body => body.Contains(filter.StopReason));
    }

    [Fact]
    public async Task RealAutoInvocationLoop_BatchedToolCalls_StopsExactlyAtTheBudget()
    {
        // Same run, but the stub packs 3 tool calls into every assistant turn. A counter checked
        // once per turn (rather than once per individual call) would let the whole batch that
        // crosses the boundary run, overshooting past 10. ToolCallBudgetFilter counts per call, so
        // the 10th call in the middle of a batch still runs and the 11th still doesn't.
        var (kernel, filter, tool, _) = BuildKernel(toolCallsPerTurn: 3);

        await RunLoopAsync(kernel);

        Assert.Equal(ToolCallBudgetFilter.MaxToolCalls, tool.Calls);
        Assert.True(filter.BudgetExhausted);
    }
}
