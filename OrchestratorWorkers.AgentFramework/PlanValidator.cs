namespace OrchestratorWorkers.AgentFramework;

public static class PlanValidator
{
    public static IReadOnlyList<string> Validate(WorkPlan plan, IReadOnlySet<string> allowedWorkers,
        int maximumTasks = 6, int maximumInstructionLength = 500)
    {
        var errors = new List<string>();
        if (plan.Tasks.Count == 0) errors.Add("Plan must contain at least one task.");
        if (plan.Tasks.Count > maximumTasks) errors.Add($"Plan exceeds the maximum of {maximumTasks} tasks.");

        foreach (var task in plan.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id)) errors.Add("Task IDs must not be empty.");
            if (!allowedWorkers.Contains(task.Worker)) errors.Add($"Worker role is not allowed: {task.Worker}.");
            if (string.IsNullOrWhiteSpace(task.Instruction) || task.Instruction.Length > maximumInstructionLength)
                errors.Add($"Task {task.Id} has an invalid instruction length.");
        }

        foreach (var duplicate in plan.Tasks.GroupBy(t => t.Id, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1).Select(g => g.Key))
            errors.Add($"Duplicate task ID: {duplicate}.");

        return errors;
    }
}
