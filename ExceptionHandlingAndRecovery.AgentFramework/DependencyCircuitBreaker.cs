namespace ExceptionHandlingAndRecovery.AgentFramework;

public enum CircuitState { Closed, Open, HalfOpen }

public sealed class BrokenCircuitException(DateTimeOffset retryAfter)
    : InvalidOperationException($"Dependency circuit is open until {retryAfter:O}.")
{
    public DateTimeOffset RetryAfter { get; } = retryAfter;
}

public sealed class DependencyCircuitBreaker
{
    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _breakDuration;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<Exception, bool> _isTransient;
    private int _consecutiveFailures;
    private DateTimeOffset _retryAfter;
    private bool _probeInProgress;

    public DependencyCircuitBreaker(int failureThreshold, TimeSpan breakDuration,
        Func<DateTimeOffset>? utcNow = null, Func<Exception, bool>? isTransient = null)
    {
        if (failureThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        if (breakDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(breakDuration));
        _failureThreshold = failureThreshold;
        _breakDuration = breakDuration;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        // A dependency timeout is a transient dependency failure, same as a 503 — provided the
        // dependency reported it as a TimeoutException rather than an ambiguous OCE.
        _isTransient = isTransient ?? (ex => ex is HttpRequestException or TimeoutException);
    }

    public CircuitState State { get; private set; }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var probe = false;
        lock (_gate)
        {
            if (State == CircuitState.Open)
            {
                if (_utcNow() < _retryAfter) throw new BrokenCircuitException(_retryAfter);
                State = CircuitState.HalfOpen;
            }
            if (State == CircuitState.HalfOpen)
            {
                if (_probeInProgress) throw new BrokenCircuitException(_retryAfter);
                _probeInProgress = probe = true;
            }
        }

        try
        {
            var result = await operation(cancellationToken);
            lock (_gate) Close();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (probe) _probeInProgress = false;
            }
            throw;
        }
        catch (Exception ex) when (!_isTransient(ex))
        {
            lock (_gate) Close();
            throw;
        }
        catch
        {
            lock (_gate)
            {
                _probeInProgress = false;
                if (State == CircuitState.HalfOpen || ++_consecutiveFailures >= _failureThreshold)
                {
                    State = CircuitState.Open;
                    _retryAfter = _utcNow() + _breakDuration;
                }
            }
            throw;
        }
    }

    private void Close()
    {
        State = CircuitState.Closed;
        _consecutiveFailures = 0;
        _probeInProgress = false;
    }
}
