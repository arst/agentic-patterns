using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ReasoningAndActing;
using Shared;

var reactKernel = new Settings().Kernel;

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
                              """);

reactHistory.AddUserMessage(
    "Which country has a larger population — Canada or Australia? " +
    "And what is the approximate ratio?");

var reactSettings = new OpenAIPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

// The agent will autonomously call GetPopulation() for each country,
// then reason about the results to compute the ratio.
var reactResponse = await reactService.GetChatMessageContentAsync(
    reactHistory, reactSettings, reactKernel);
Console.WriteLine($"ReAct Agent:\n{reactResponse.Content}\n");