using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace IdempotentToolCalls.AgentFramework;

public sealed class IdempotencyStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int CompletedOperationCount => _entries.Values.Count(e => e.State == IdempotencyOperationState.Completed);

    public async Task<T> ExecuteAsync<T>(string key, string normalizedRequest,
        Func<CancellationToken, Task<T>> operation, bool loseResponseAfterCommit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An idempotency key is required.", nameof(key));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRequest)));
        var entry = _entries.GetOrAdd(key, _ => new Entry(hash));
        if (entry.RequestHash != hash)
            throw new IdempotencyConflictException(key);

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.PermanentFailure is { } failure) throw new PermanentToolException(failure);
            if (entry.State == IdempotencyOperationState.Completed) return (T)entry.Result!;

            try
            {
                entry.State = IdempotencyOperationState.InProgress;
                entry.Result = await operation(cancellationToken);
                entry.State = IdempotencyOperationState.Completed;
            }
            catch (PermanentToolException ex)
            {
                entry.PermanentFailure = ex.Message;
                entry.State = IdempotencyOperationState.PermanentlyFailed;
                throw;
            }
            catch
            {
                entry.State = IdempotencyOperationState.Pending;
                throw;
            }

            if (loseResponseAfterCommit && !entry.LostResponseInjected)
            {
                entry.LostResponseInjected = true;
                throw new HttpRequestException("Simulated response loss after the operation committed.");
            }

            return (T)entry.Result!;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class Entry(string requestHash)
    {
        public string RequestHash { get; } = requestHash;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object? Result { get; set; }
        public string? PermanentFailure { get; set; }
        public IdempotencyOperationState State { get; set; }
        public bool LostResponseInjected { get; set; }
    }
}

public enum IdempotencyOperationState { Pending, InProgress, Completed, PermanentlyFailed }

public sealed class IdempotencyConflictException(string key)
    : InvalidOperationException($"Idempotency key '{key}' was already used for a different request.");

public sealed class PermanentToolException(string message) : Exception(message);
