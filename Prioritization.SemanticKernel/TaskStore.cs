using System.Collections.Concurrent;

internal static class TaskStore
{
    private static readonly ConcurrentDictionary<string, ProjectTask> _tasks = new();
    private static int _nextId = 1;

    public static ProjectTask Create(string description)
    {
        var id = $"TASK-{Interlocked.Increment(ref _nextId):D3}";
        var task = new ProjectTask(id, description, "P2", null, "open", DateTime.UtcNow);
        _tasks[id] = task;
        return task;
    }

    public static ProjectTask? SetPriority(string taskId, string priority)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return null;
        var updated = task with { Priority = priority };
        _tasks[taskId] = updated;
        return updated;
    }

    public static ProjectTask? Assign(string taskId, string worker)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return null;
        var updated = task with { AssignedTo = worker, Status = "assigned" };
        _tasks[taskId] = updated;
        return updated;
    }

    public static IEnumerable<ProjectTask> GetAllSorted()
    {
        return _tasks.Values
            .OrderBy(t => t.Priority) // P0 < P1 < P2
            .ThenBy(t => t.Created);
        // Oldest first within same priority
    }

    public static ProjectTask? GetNextUnassigned()
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