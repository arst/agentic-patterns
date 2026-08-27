using IdempotentToolCalls.AgentFramework;
using Microsoft.Extensions.AI;

var service = new SimulatedRefundService();               // the remote side-effect owner
var idempotencyKey = Guid.NewGuid().ToString("N");        // minted by the trusted host, not the model
var arguments = new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = 25m };

AIFunction Refund(IdempotentTool tool, bool loseResponse) => AIFunctionFactory.Create(
    (string orderId, decimal amount, CancellationToken ct) =>
        tool.IssueRefundAsync(orderId, amount, idempotencyKey, loseResponse, ct),
    "IssueRefund", "Issue one retry-safe refund; the trusted host supplies the idempotency key.");

Console.WriteLine("=== Attempt 1: the refund commits, then the connection drops ===");
try
{
    await Refund(new IdempotentTool(service), loseResponse: true).InvokeAsync(arguments);
}
catch (HttpRequestException ex)
{
    Console.WriteLine(ex.Message);
}
Console.WriteLine($"Refunds committed remotely: {service.Refunds.Count} (the caller does not know this)");

// A fresh caller INSTANCE, not a fresh process: this program never restarts, and the same
// SimulatedRefundService object is still in memory. That is enough to make the point, because
// the caller holds no deduplication state to begin with — the key and the record both live with
// the side-effect owner. Making it literally cross-process (two `dotnet run` invocations sharing
// the same key, the service persisting its records to disk) would only move the same boundary
// behind a file; see DurableExecution for that shape.
Console.WriteLine("\n=== Attempt 2: a fresh caller instance with no caller-side state, same key ===");
var retry = await Refund(new IdempotentTool(service), loseResponse: false).InvokeAsync(arguments);
Console.WriteLine($"Result: {retry}");
Console.WriteLine($"Refund side effects: {service.Refunds.Count} (expected: 1)");

Console.WriteLine("\n=== Conflict: same key, different request ===");
try
{
    await new IdempotentTool(service).IssueRefundAsync("ORD-100", 30m, idempotencyKey);
}
catch (IdempotencyConflictException ex)
{
    Console.WriteLine(ex.Message);
}
