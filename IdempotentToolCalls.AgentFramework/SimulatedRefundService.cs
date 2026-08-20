using System.Collections.Concurrent;

namespace IdempotentToolCalls.AgentFramework;

public sealed record Refund(string Id, string OrderId, decimal Amount);

public sealed class SimulatedRefundService
{
    private readonly ConcurrentBag<Refund> _refunds = [];

    public IReadOnlyCollection<Refund> Refunds => _refunds;

    public Task<Refund> CreateAsync(string orderId, decimal amount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (amount <= 0) throw new PermanentToolException("Refund amount must be positive.");
        var refund = new Refund($"REF-{Guid.NewGuid():N}", orderId, amount);
        _refunds.Add(refund);
        return Task.FromResult(refund);
    }
}
