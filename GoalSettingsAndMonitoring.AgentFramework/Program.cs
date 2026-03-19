using GoalSettingsAndMonitoring.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

const int maxIterations = 5;

async Task<AgentResponse> GoalDirectedMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

    for (var iteration = 1; iteration <= maxIterations; iteration++)
    {
        Console.WriteLine($"  [GoalMonitor] Iteration {iteration}/{maxIterations}");

        var responseText = string.Join(" ",
            response.Messages.Select(m => m.Text ?? ""));

        if (responseText.Contains("\"allGoalsMet\": true") ||
            responseText.Contains("All goals met", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  [GoalMonitor] Goals achieved — returning result.");
            return response; // Early return — middleware terminates the loop
        }

        if (iteration >= maxIterations)
        {
            Console.WriteLine("  [GoalMonitor] Max iterations — returning best effort.");
            return response;
        }

        Console.WriteLine("[GoalMonitor] Goals not met — requesting refinement...\n");

        var refinementMessage = new ChatMessage(ChatRole.User,
            "The goals are not fully met. Review the EvaluateGoals feedback and refine your code. " +
            "Then call EvaluateGoals again.");

        response = await innerAgent.RunAsync(
            [refinementMessage], session, options, cancellationToken);
    }

    return response;
}


var chatClient = Settings.ChatClient;
var agent = new ChatClientAgent(chatClient,
        $$"""
          You are a code generation agent working toward specific goals.

          YOUR GOALS:
          {{GoalDefinition.GoalsAsText}}

          PROCESS:
          1. Generate or refine C# code for the requested task.
          2. ALWAYS call EvaluateGoals with your generated code.
          3. If goals are not met, read the feedback and refine.
          4. Present final code only after EvaluateGoals confirms allGoalsMet: true.
          """,
        "GoalDrivenCodeAgent",
        tools: [AIFunctionFactory.Create(CodeGenerationPlugin.EvaluateGoals)])
    .AsBuilder()
    .Use(GoalDirectedMiddleware, null)
    .Build();

var session = await agent.CreateSessionAsync();

Console.WriteLine("User: Write a C# method that parses a string to an integer safely.\n");

// Single call — the goal-directed middleware handles all iterations internally
var result = await agent.RunAsync(
    "Write a C# method that parses a string to an integer safely.",
    session);

Console.WriteLine($"\nFinal output:\n{result}");