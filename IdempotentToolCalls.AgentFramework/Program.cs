using IdempotentToolCalls.AgentFramework;
using Microsoft.Extensions.AI;

var store = new IdempotencyStore();
var service = new SimulatedRefundService();
var tool = new IdempotentTool(store, service);
var idempotencyKey = Guid.NewGuid().ToString("N"); // trusted host orchestration owns this key
var firstAttempt = true;

var issueRefund = AIFunctionFactory.Create(async (string orderId, decimal amount, CancellationToken cancellationToken) =>
{
    var loseResponse = firstAttempt;
    firstAttempt = false;
    return await tool.IssueRefundAsync(orderId, amount, idempotencyKey, loseResponse, cancellationToken);
}, "IssueRefund", "Issue one retry-safe refund; the trusted host supplies the idempotency key.");
var arguments = new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = 25m };

Console.WriteLine("=== First attempt: commit succeeds, response is lost ===");
try
{
    await issueRefund.InvokeAsync(arguments);
}
catch (HttpRequestException ex)
{
    Console.WriteLine(ex.Message);
}

Console.WriteLine("\n=== Retry: same key and request ===");
var retry = await issueRefund.InvokeAsync(arguments);
Console.WriteLine($"Result: {retry}");
Console.WriteLine($"Refund side effects: {service.Refunds.Count} (expected: 1)");

Console.WriteLine("\n=== Conflict: same key, different request ===");
try
{
    await tool.IssueRefundAsync("ORD-100", 30m, idempotencyKey);
}
catch (IdempotencyConflictException ex)
{
    Console.WriteLine(ex.Message);
}
