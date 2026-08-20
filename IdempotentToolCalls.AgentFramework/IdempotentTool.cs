using System.Globalization;

namespace IdempotentToolCalls.AgentFramework;

public sealed class IdempotentTool(IdempotencyStore store, SimulatedRefundService refunds)
{
    public Task<Refund> IssueRefundAsync(string orderId, decimal amount, string idempotencyKey,
        bool loseResponseAfterCommit = false, CancellationToken cancellationToken = default)
    {
        var normalizedOrder = orderId.Trim().ToUpperInvariant();
        var normalizedRequest = $"IssueRefund|{normalizedOrder}|{amount.ToString("F2", CultureInfo.InvariantCulture)}";
        return store.ExecuteAsync(idempotencyKey, normalizedRequest,
            ct => refunds.CreateAsync(normalizedOrder, amount, ct), loseResponseAfterCommit, cancellationToken);
    }
}
