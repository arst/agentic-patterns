namespace ReversibleActionCompensation.AgentFramework;

public sealed record CompensableStep(
    string Name,
    Action<string> Apply,
    Action<string> Compensate);

public sealed record SagaEvent(string Step, string Phase, bool Succeeded, string? Error = null);

public enum SagaStatus
{
    Completed,
    Compensated,
    CompensationFailed
}

public sealed record SagaResult(SagaStatus Status, IReadOnlyList<SagaEvent> Events);

public sealed class SagaRunner
{
    // ponytail: in-memory dedup is enough for the sample; use a transactional durable store
    // shared with each side effect when work must survive process or machine failure.
    private readonly Dictionary<string, SagaResult> results = new(StringComparer.Ordinal);

    public SagaResult Run(string sagaId, IReadOnlyList<CompensableStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaId);
        ArgumentNullException.ThrowIfNull(steps);

        // ponytail: one process-wide lock makes concurrent retries safe in this teaching sample;
        // partition the durable ledger when independent sagas need production throughput.
        lock (results)
            return RunOnce(sagaId, steps);
    }

    private SagaResult RunOnce(string sagaId, IReadOnlyList<CompensableStep> steps)
    {
        if (results.TryGetValue(sagaId, out var existing))
            return existing;

        var events = new List<SagaEvent>();
        var completed = new List<(CompensableStep Step, int Index)>();

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            try
            {
                step.Apply($"{sagaId}:{index}:apply");
                completed.Add((step, index));
                events.Add(new(step.Name, "apply", true));
            }
            catch (Exception error)
            {
                events.Add(new(step.Name, "apply", false, error.Message));
                var compensationFailed = false;

                foreach (var (appliedStep, appliedIndex) in completed.AsEnumerable().Reverse())
                {
                    try
                    {
                        appliedStep.Compensate($"{sagaId}:{appliedIndex}:compensate");
                        events.Add(new(appliedStep.Name, "compensate", true));
                    }
                    catch (Exception compensationError)
                    {
                        compensationFailed = true;
                        events.Add(new(appliedStep.Name, "compensate", false, compensationError.Message));
                    }
                }

                return Remember(sagaId, new(
                    compensationFailed ? SagaStatus.CompensationFailed : SagaStatus.Compensated,
                    events));
            }
        }

        return Remember(sagaId, new(SagaStatus.Completed, events));
    }

    private SagaResult Remember(string sagaId, SagaResult result)
    {
        results.Add(sagaId, result);
        return result;
    }
}
