namespace OrchestratorWorkers.AgentFramework;

public sealed class WorkerRegistry
{
    private readonly Dictionary<string, Func<WorkerTask, CancellationToken, Task<string>>> _workers =
        new(StringComparer.Ordinal);

    public IReadOnlySet<string> Roles => _workers.Keys.ToHashSet(StringComparer.Ordinal);

    public void Register(string role, Func<WorkerTask, CancellationToken, Task<string>> worker) =>
        _workers.Add(role, worker);

    public async Task<IReadOnlyList<WorkerResult>> ExecuteAsync(WorkPlan plan, int maximumConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (maximumConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        using var slots = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        return await Task.WhenAll(plan.Tasks.Select(async task =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                if (!_workers.TryGetValue(task.Worker, out var worker))
                    return new WorkerResult(task.Id, task.Worker, null, "Worker role is not registered.");
                try
                {
                    return new WorkerResult(task.Id, task.Worker,
                        await worker(task, cancellationToken), null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new WorkerResult(task.Id, task.Worker, null, ex.Message);
                }
            }
            finally
            {
                slots.Release();
            }
        }));
    }

    public static string BuildSynthesisInput(IEnumerable<WorkerResult> results) =>
        string.Join("\n\n", results.Where(r => r.Succeeded)
            .Select(r => $"## {r.TaskId} ({r.Worker})\n{r.Output}"));
}
