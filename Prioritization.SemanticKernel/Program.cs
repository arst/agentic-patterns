using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shared;

var kernel = Settings.Kernel;

kernel.Plugins.AddFromType<ProjectTools>();

ChatCompletionAgent agent = new()
{
    Name = "ProjectManager",
    Instructions = """
                   You are an AI Project Manager. You manage tasks for a development team.

                   PRIORITIZATION CRITERIA (use these to assign P0/P1/P2):
                   - P0 (Critical): Production outages, security vulnerabilities, blocking issues
                     that prevent the team from working. Must be addressed immediately.
                   - P1 (Important): Features for upcoming release, significant bugs affecting users,
                     tasks that other tasks depend on. Should be done this sprint.
                   - P2 (Normal): Nice-to-haves, minor improvements, tech debt, documentation.
                     Do when capacity allows.

                   WORKFLOW:
                   1. When given a list of work items, create tasks for each one.
                   2. Analyze each task and set its priority based on the criteria above.
                   3. List all tasks sorted by priority to confirm the ordering.
                   4. Assign tasks to available team members, starting with the highest priority.

                   DYNAMIC RE-PRIORITIZATION:
                   - If a new urgent issue arrives, re-evaluate ALL existing task priorities.
                   - A task blocking others should be bumped up in priority.
                   - Always explain your prioritization reasoning briefly.

                   Available team members: Alice (backend), Bob (frontend), Carol (DevOps).
                   """,
    Kernel = kernel,
    Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
    {
        FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
    })
};

// Run the workflow - agent autonomously creates, prioritizes, assigns

var thread = new ChatHistoryAgentThread();

Console.WriteLine("--- Step 1: Initial task batch ---\n");

await foreach (var response in agent.InvokeAsync("""
                                                 We have the following work items for this sprint:
                                                 1. Add dark mode to the settings page
                                                 2. Fix the login timeout bug — users are getting logged out after 2 minutes
                                                 3. Update the API documentation for v2 endpoints
                                                 4. The checkout page is returning 500 errors in production
                                                 5. Migrate CI/CD pipeline to GitHub Actions

                                                 Please create tasks, prioritize them, and assign to the team.
                                                 """, thread))
    Console.WriteLine(response.Message.Content);

Console.WriteLine("\n\n--- Step 2: Urgent issue arrives — re-prioritize ---\n");

await foreach (var response in agent.InvokeAsync("""
                                                 URGENT: We just discovered a SQL injection vulnerability in the user search endpoint.
                                                 This needs immediate attention. Please create a task for this, re-evaluate all
                                                 priorities, and reassign if needed.
                                                 """, thread))
    Console.WriteLine(response.Message.Content);

Console.WriteLine("\n\n--- Step 3: Final task board ---\n");

await foreach (var response in agent.InvokeAsync(
                   "Show me the current task board sorted by priority.", thread))
    Console.WriteLine(response.Message.Content);