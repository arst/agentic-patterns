using System.ComponentModel;
using Microsoft.SemanticKernel;

public class ProjectTools
{
    [KernelFunction]
    [Description("Create a new project task. Returns the task ID.")]
    public static string CreateTask(
        [Description("Description of the task")]
        string description)
    {
        var task = TaskStore.Create(description);
        return $"Created {task.Id}: '{task.Description}' (priority: {task.Priority})";
    }

    [KernelFunction]
    [Description("Set the priority of a task. Priority must be P0 (critical), P1 (important), or P2 (normal).")]
    public static string SetTaskPriority(
        [Description("Task ID, e.g. TASK-001")]
        string taskId,
        [Description("Priority: P0, P1, or P2")]
        string priority)
    {
        var task = TaskStore.SetPriority(taskId, priority);
        return task != null
            ? $"Set {taskId} priority to {priority}."
            : $"Task {taskId} not found.";
    }

    [KernelFunction]
    [Description("Assign a task to a team member.")]
    public static string AssignTask(
        [Description("Task ID")] string taskId,
        [Description("Name of the team member")]
        string workerName)
    {
        var task = TaskStore.Assign(taskId, workerName);
        return task != null
            ? $"Assigned {taskId} to {workerName}."
            : $"Task {taskId} not found.";
    }

    [KernelFunction]
    [Description("List all tasks sorted by priority (P0 first, then P1, then P2). Shows status and assignee.")]
    public static string ListTasks()
    {
        var tasks = TaskStore.GetAllSorted().ToList();
        if (tasks.Count == 0) return "No tasks in the system.";

        return string.Join("\n", tasks.Select(t =>
            $"  {t.Id} [{t.Priority}] {t.Description} — {t.Status}" +
            (t.AssignedTo != null ? $" (→ {t.AssignedTo})" : "")));
    }

    [KernelFunction]
    [Description("Get the next highest-priority unassigned task.")]
    public static string GetNextTask()
    {
        var task = TaskStore.GetNextUnassigned();
        return task != null
            ? $"Next: {task.Id} [{task.Priority}] {task.Description}"
            : "No unassigned tasks remaining.";
    }
}