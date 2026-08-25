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

Console.WriteLine("\n=== Attempt 2: a FRESH caller process, no local state, same key ===");
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
