using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IdempotentToolCalls.AgentFramework;

public sealed record Refund(string Id, string OrderId, decimal Amount);

/// <summary>
/// The side-effect OWNER. The refund and its idempotency record are committed together, so a
/// caller that never learns the outcome can retry with the same key and get the original refund.
/// A client-side registry cannot do this: the hard window is "committed remotely, unknown locally".
/// </summary>
public sealed class SimulatedRefundService
{
    // ponytail: one dictionary + one gate stands in for a database row and its transaction.
    // Swap for a unique index on (tenant, key) and a single INSERT..RETURNING in production.
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Refund> _refunds = [];

    public IReadOnlyCollection<Refund> Refunds => _refunds;

    public async Task<Refund> IssueRefundAsync(string tenantId, string idempotencyKey, string orderId,
        decimal amount, bool loseResponseAfterCommit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        var normalizedOrder = orderId.Trim().ToUpperInvariant();
        var request = $"IssueRefund|{normalizedOrder}|{amount.ToString("F2", CultureInfo.InvariantCulture)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
        var entry = _entries.GetOrAdd($"{tenantId}|{idempotencyKey}", _ => new Entry(hash));
        if (entry.RequestHash != hash) throw new IdempotencyConflictException(idempotencyKey);

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.PermanentFailure is { } failure) throw new PermanentToolException(failure);
            if (entry.Refund is { } completed) return Deliver(completed, entry, loseResponseAfterCommit);

            if (amount <= 0)
            {
                entry.PermanentFailure = "Refund amount must be positive.";
                throw new PermanentToolException(entry.PermanentFailure);
            }

            // Commit point: the refund and the idempotency record become durable together.
            var refund = new Refund($"REF-{Guid.NewGuid():N}", normalizedOrder, amount);
            _refunds.Add(refund);
            entry.Refund = refund;

            return Deliver(refund, entry, loseResponseAfterCommit);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // The failure the pattern exists for: committed, then the caller never hears back.
    private static Refund Deliver(Refund refund, Entry entry, bool loseResponseAfterCommit)
    {
        if (loseResponseAfterCommit && !entry.LostResponseInjected)
        {
            entry.LostResponseInjected = true;
            throw new HttpRequestException("Connection lost after the refund committed.");
        }
        return refund;
    }

    private sealed class Entry(string requestHash)
    {
        public string RequestHash { get; } = requestHash;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Refund? Refund { get; set; }
        public string? PermanentFailure { get; set; }
        public bool LostResponseInjected { get; set; }
    }
}

public sealed class IdempotencyConflictException(string key)
    : InvalidOperationException($"Idempotency key '{key}' was already used for a different request.");

public sealed class PermanentToolException(string message) : Exception(message);
