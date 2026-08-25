using System.Diagnostics;

namespace BoundedExecution.AgentFramework;

public sealed class ExecutionBudgetState
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, int> _toolCalls = new(StringComparer.Ordinal);
    private long _reservedInputTokens;
    private long _reservedOutputTokens;
    private decimal _reservedCost;

    public ExecutionBudgetState(ExecutionBudget budget)
    {
        if (budget.MaxIterations <= 0 || budget.MaxModelCalls <= 0 || budget.MaxToolCalls <= 0 ||
            budget.MaxInputTokens <= 0 || budget.MaxOutputTokens <= 0 || budget.MaxElapsedTime <= TimeSpan.Zero ||
            budget.MaxEstimatedCost <= 0 || budget.SoftThreshold is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(budget), "Budget limits must be positive and the soft threshold must be between 0 and 1.");
        Budget = budget;
    }

    public ExecutionBudget Budget { get; }
    public int Iterations { get; private set; }
    public int ModelCalls { get; private set; }
    public int ToolCalls { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public decimal EstimatedCost { get; private set; }

    public void RecordIteration()
    {
        lock (_gate)
        {
            ThrowIfElapsed();
            ThrowIf(Iterations + 1 > Budget.MaxIterations, StopReason.IterationLimitReached);
            Iterations++;
        }
    }

    public long RemainingOutputTokens
    {
        get { lock (_gate) return Budget.MaxOutputTokens - OutputTokens - _reservedOutputTokens; }
    }

    public ModelCallReservation ReserveModelCall(long maximumInputTokens, long maximumOutputTokens, decimal maximumCost)
    {
        lock (_gate)
        {
            ThrowIfElapsed();
            ThrowIf(ModelCalls + 1 > Budget.MaxModelCalls, StopReason.ModelCallLimitReached);
            ThrowIf(InputTokens + _reservedInputTokens + maximumInputTokens > Budget.MaxInputTokens,
                StopReason.InputTokenLimitReached);
            ThrowIf(OutputTokens + _reservedOutputTokens + maximumOutputTokens > Budget.MaxOutputTokens,
                StopReason.OutputTokenLimitReached);
            ThrowIf(EstimatedCost + _reservedCost + maximumCost > Budget.MaxEstimatedCost,
                StopReason.EstimatedCostLimitReached);

            ModelCalls++;
            _reservedInputTokens += maximumInputTokens;
            _reservedOutputTokens += maximumOutputTokens;
            _reservedCost += maximumCost;
            return new ModelCallReservation(maximumInputTokens, maximumOutputTokens, maximumCost);
        }
    }

    // Usage a provider did not report is charged at the reservation, never at zero - otherwise a
    // provider that omits usage silently disables the token ceiling.
    public void Reconcile(ModelCallReservation reservation, long? inputTokens, long? outputTokens,
        Func<long, long, decimal> price)
    {
        var input = inputTokens ?? reservation.InputTokens;
        var output = outputTokens ?? reservation.OutputTokens;
        lock (_gate)
        {
            Complete(reservation);
            InputTokens += input;
            OutputTokens += output;
            EstimatedCost += price(input, output);
            ThrowIf(InputTokens > Budget.MaxInputTokens, StopReason.InputTokenLimitReached);
            ThrowIf(OutputTokens > Budget.MaxOutputTokens, StopReason.OutputTokenLimitReached);
            ThrowIf(EstimatedCost > Budget.MaxEstimatedCost, StopReason.EstimatedCostLimitReached);
        }
    }

    public void Release(ModelCallReservation reservation)
    {
        lock (_gate)
            Complete(reservation);
    }

    public void RecordToolCall(string toolName, int? perToolLimit = null)
    {
        lock (_gate)
        {
            ThrowIfElapsed();
            ThrowIf(ToolCalls + 1 > Budget.MaxToolCalls, StopReason.ToolCallLimitReached);
            var calls = _toolCalls.GetValueOrDefault(toolName) + 1;
            ThrowIf(perToolLimit is not null && calls > perToolLimit, StopReason.ToolCallLimitReached);
            _toolCalls[toolName] = calls;
            ToolCalls++;
        }
    }

    public TimeSpan RemainingTime
    {
        get
        {
            lock (_gate)
            {
                var remaining = Budget.MaxElapsedTime - _clock.Elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    public CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(RemainingTime);
        return source;
    }

    public BudgetSnapshot Snapshot()
    {
        lock (_gate)
        {
            var soft = Iterations >= Budget.MaxIterations * Budget.SoftThreshold ||
                       ModelCalls >= Budget.MaxModelCalls * Budget.SoftThreshold ||
                       ToolCalls >= Budget.MaxToolCalls * Budget.SoftThreshold ||
                       InputTokens + _reservedInputTokens >= Budget.MaxInputTokens * Budget.SoftThreshold ||
                       OutputTokens + _reservedOutputTokens >= Budget.MaxOutputTokens * Budget.SoftThreshold ||
                       EstimatedCost + _reservedCost >= Budget.MaxEstimatedCost * Budget.SoftThreshold ||
                       _clock.Elapsed >= Budget.MaxElapsedTime * (double)Budget.SoftThreshold;
            return new BudgetSnapshot(Iterations, ModelCalls, ToolCalls, InputTokens, OutputTokens,
                _clock.Elapsed, EstimatedCost, soft);
        }
    }

    private void Complete(ModelCallReservation reservation)
    {
        if (reservation.Completed) return;
        reservation.Completed = true;
        _reservedInputTokens -= reservation.InputTokens;
        _reservedOutputTokens -= reservation.OutputTokens;
        _reservedCost -= reservation.Cost;
    }

    private void ThrowIfElapsed() =>
        ThrowIf(_clock.Elapsed >= Budget.MaxElapsedTime, StopReason.ElapsedTimeLimitReached);

    private void ThrowIf(bool condition, StopReason reason)
    {
        if (condition) throw new BudgetExceededException(reason, Snapshot());
    }
}

public sealed class ModelCallReservation(long inputTokens, long outputTokens, decimal cost)
{
    public long InputTokens { get; } = inputTokens;
    public long OutputTokens { get; } = outputTokens;
    public decimal Cost { get; } = cost;
    internal bool Completed { get; set; }
}
