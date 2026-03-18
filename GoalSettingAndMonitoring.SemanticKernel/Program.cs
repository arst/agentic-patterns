using GoalSettingAndMonitoring.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shared;

var builder = new Settings().KernelBuilder;

builder.Plugins.AddFromType<CodeGenerationPlugin>();

builder.Services.AddSingleton<IAutoFunctionInvocationFilter, GoalMonitoringFilter>();

var kernel = builder.Build();
var chat = kernel.GetRequiredService<IChatCompletionService>();

var settings = new PromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

var history = new ChatHistory(
    $$"""
      You are a code generation agent working toward specific goals.

      YOUR GOALS:
      {{GoalDefinition.GoalsAsText}}

      PROCESS:
      1. Generate or refine C# code for the requested task.
      2. ALWAYS call EvaluateGoals with your generated code to check progress.
      3. If goals are not met, read the feedback and refine your code.
      4. Repeat until all goals are met.

      Do NOT present a final answer until EvaluateGoals returns allGoalsMet: true.
      """);

Console.WriteLine("User: Write a C# method that parses a string to an integer safely.\n");
history.AddUserMessage("Write a C# method that parses a string to an integer safely.");

var response = await chat.GetChatMessageContentAsync(history, settings, kernel);

Console.WriteLine($"\n🤖 Final output:\n{response.Content}");