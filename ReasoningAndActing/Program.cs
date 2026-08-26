using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ReasoningAndActing;
using Shared;

// Local kernel so the shared Settings.Kernel singleton stays unmodified
var reactBuilder = Settings.CreateKernelBuilder();
// One filter instance for this one run: the counter lives on the instance, so registering the
// instance (not the type) keeps the budget per-run rather than per-process.
var toolCallBudget = new ToolCallBudgetFilter();
reactBuilder.Services.AddSingleton<IAutoFunctionInvocationFilter>(toolCallBudget);
var reactKernel = reactBuilder.Build();

// Register tools the agent can use mid-reasoning
reactKernel.Plugins.AddFromType<ResearchTools>();

var reactService = reactKernel.GetRequiredService<IChatCompletionService>();

var reactHistory = new ChatHistory();
reactHistory.AddSystemMessage("""
                              You are a research agent that reasons step by step and uses tools to gather facts.
                              For each question:
                              1. Think about what information you need.
                              2. Use the available tools to look up specific facts.
                              3. Reason about the tool results to draw conclusions.
                              4. If the tool result is insufficient, think about what else to look up.
                              5. Synthesize all gathered information into a final answer.

                              Always explain your reasoning between tool calls.
                              Use at most 10 tool calls before giving your final answer.
                              """);

reactHistory.AddUserMessage(
    "Which country has a larger population — Canada or Australia? " +
    "And what is the approximate ratio?");

var reactSettings = new OpenAIPromptExecutionSettings
{
    // SK 1.79 exposes no max-auto-invoke option on FunctionChoiceBehavior/FunctionChoiceBehaviorOptions,
    // so the "at most 10 tool calls" instruction above is only a hint to the model. ToolCallBudgetFilter,
    // registered on reactKernel above, is the actual control: the 11th call is refused and the
    // auto-invocation loop is terminated, regardless of what the model intends to do next.
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

// The agent will autonomously call GetPopulation() for each country,
// then reason about the results to compute the ratio.
var reactResponse = await reactService.GetChatMessageContentAsync(
    reactHistory, reactSettings, reactKernel);

if (toolCallBudget.BudgetExhausted)
{
    // Same shape as BoundedExecution: a PARTIAL result, the stop reason, and an explicit
    // incomplete label rather than silently truncated output. SK returns normally after
    // Terminate (with empty content), so the filter's flag — not an exception — is the signal.
    Console.WriteLine("Result status: PARTIAL");
    Console.WriteLine($"Stop reason: {toolCallBudget.StopReason}");
    Console.WriteLine("ReAct Agent:\nStopped at the tool-call budget before a final answer was reached; " +
                      "any reasoning gathered so far is incomplete.\n");
}
else
{
    Console.WriteLine($"ReAct Agent:\n{reactResponse.Content}\n");
}
