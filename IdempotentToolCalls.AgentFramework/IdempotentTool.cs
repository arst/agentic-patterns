namespace IdempotentToolCalls.AgentFramework;

/// The client holds NO deduplication state. It only carries the key the trusted host minted.
public sealed class IdempotentTool(SimulatedRefundService refunds, string tenantId = "tenant-a")
{
    public Task<Refund> IssueRefundAsync(string orderId, decimal amount, string idempotencyKey,
        bool loseResponseAfterCommit = false, CancellationToken cancellationToken = default) =>
        refunds.IssueRefundAsync(tenantId, idempotencyKey, orderId, amount, loseResponseAfterCommit,
            cancellationToken);
}
