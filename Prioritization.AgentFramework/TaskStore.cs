using System.Collections.Concurrent;

namespace Prioritization.AgentFramework;

internal class TaskStore
{
    private readonly ConcurrentDictionary<string, ProjectTask> _tasks = new();
    private int _nextId;

    public ProjectTask Create(string description)
    {
        var id = $"TASK-{Interlocked.Increment(ref _nextId):D3}";
        var task = new ProjectTask(id, description, "P2", null, "open", DateTime.UtcNow);
        _tasks[id] = task;
        return task;
    }

    public ProjectTask? SetPriority(string taskId, string priority)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return null;
        var updated = task with { Priority = priority };
        _tasks[taskId] = updated;
        return updated;
    }

    public ProjectTask? Assign(string taskId, string worker)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return null;
        var updated = task with { AssignedTo = worker, Status = "assigned" };
        _tasks[taskId] = updated;
        return updated;
    }

    public IEnumerable<ProjectTask> GetAllSorted()
    {
        return _tasks.Values.OrderBy(t => t.Priority).ThenBy(t => t.Created);
    }

    public ProjectTask? GetNextUnassigned()
    {
        return GetAllSorted().FirstOrDefault(t => t.Status == "open");
    }

    public record ProjectTask(
        string Id,
        string Description,
        string Priority,
        string? AssignedTo,
        string Status,
        DateTime Created);
}