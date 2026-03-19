using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Prioritization.AgentFramework;
using Shared;

var taskStore = new TaskStore();

[Description("Create a new project task. Returns the task ID.")]
string CreateTask([Description("Description of the task")] string description)
{
    var task = taskStore.Create(description);
    return $"Created {task.Id}: '{task.Description}' (priority: {task.Priority})";
}

[Description("Set the priority of a task. Priority must be P0 (critical), P1 (important), or P2 (normal).")]
string SetTaskPriority(
    [Description("Task ID, e.g. TASK-001")]
    string taskId,
    [Description("Priority: P0, P1, or P2")]
    string priority)
{
    var task = taskStore.SetPriority(taskId, priority);
    return task != null ? $"Set {taskId} priority to {priority}." : $"Task {taskId} not found.";
}

[Description("Assign a task to a team member.")]
string AssignTask(
    [Description("Task ID")] string taskId,
    [Description("Name of the team member")]
    string workerName)
{
    var task = taskStore.Assign(taskId, workerName);
    return task != null ? $"Assigned {taskId} to {workerName}." : $"Task {taskId} not found.";
}

[Description("List all tasks sorted by priority (P0 first). Shows status and assignee.")]
string ListTasks()
{
    var tasks = taskStore.GetAllSorted().ToList();
    if (tasks.Count == 0) return "No tasks in the system.";
    return string.Join("\n", tasks.Select(t =>
        $"  {t.Id} [{t.Priority}] {t.Description} — {t.Status}" +
        (t.AssignedTo != null ? $" (? {t.AssignedTo})" : "")));
}

[Description("Get the next highest-priority unassigned task.")]
string GetNextTask()
{
    var task = taskStore.GetNextUnassigned();
    return task != null
        ? $"Next: {task.Id} [{task.Priority}] {task.Description}"
        : "No unassigned tasks remaining.";
}

var agent = new ChatClientAgent(Settings.ChatClient,
    name: "ProjectManager",
    instructions: """
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
    tools:
    [
        AIFunctionFactory.Create(CreateTask),
        AIFunctionFactory.Create(SetTaskPriority),
        AIFunctionFactory.Create(AssignTask),
        AIFunctionFactory.Create(ListTasks),
        AIFunctionFactory.Create(GetNextTask)
    ]);

var session = await agent.CreateSessionAsync();

Console.WriteLine("---- Step 1: Initial task batch ----\n");

var result1 = await agent.RunAsync("""
                                   We have the following work items for this sprint:
                                   1. Add dark mode to the settings page
                                   2. Fix the login timeout bug — users are getting logged out after 2 minutes
                                   3. Update the API documentation for v2 endpoints
                                   4. The checkout page is returning 500 errors in production
                                   5. Migrate CI/CD pipeline to GitHub Actions

                                   Please create tasks, prioritize them, and assign to the team.
                                   """, session);
Console.WriteLine(result1);

Console.WriteLine("\n\n---- Step 2: Urgent issue arrives — re-prioritize ----\n");

var result2 = await agent.RunAsync("""
                                   URGENT: We just discovered a SQL injection vulnerability in the user search endpoint.
                                   This needs immediate attention. Please create a task for this, re-evaluate all
                                   priorities, and reassign if needed.
                                   """, session);
Console.WriteLine(result2);

Console.WriteLine("\n\n---- Step 3: Final task board ----\n");

var result3 = await agent.RunAsync(
    "Show me the current task board sorted by priority.", session);
Console.WriteLine(result3);