using Microsoft.Extensions.AI;
using ToolAuthorization.AgentFramework;

var principal = new RunPrincipal("CUSTOMER-100", "TENANT-EU");
var orderOwners = new Dictionary<string, RunPrincipal>(StringComparer.OrdinalIgnoreCase)
{
    ["ORD-100"] = principal,
    ["ORD-999"] = new("CUSTOMER-999", "TENANT-EU")
};
var policy = new ToolAuthorizationPolicy(orderOwners);

ToolCapability Grant(string tool, decimal? maximumAmount = null) => new(
    principal.SubjectId, principal.TenantId, tool, new Dictionary<string, string>(), maximumAmount,
    DateTimeOffset.UtcNow.AddMinutes(5), Guid.NewGuid().ToString("N"));

var getOrder = new AuthorizedAIFunction(
    AIFunctionFactory.Create((string orderId) => $"{orderId}: paid, awaiting shipment", "GetOrder"),
    principal, Grant("GetOrder"), policy);
var issueRefund = new AuthorizedAIFunction(
    AIFunctionFactory.Create((string orderId, decimal amount) => $"Refunded €{amount:F2} for {orderId}", "IssueRefund"),
    principal, Grant("IssueRefund", maximumAmount: 50m), policy);

async Task Show(AIFunction tool, AIFunctionArguments arguments)
{
    Console.WriteLine($"\n{tool.Name}({string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}"))})");
    Console.WriteLine($"  result: {await tool.InvokeAsync(arguments)}");
}

Console.WriteLine("=== Capability-scoped tools ===");
await Show(getOrder, new AIFunctionArguments { ["orderId"] = "ORD-100" });
await Show(getOrder, new AIFunctionArguments { ["orderId"] = "ORD-999" });
await Show(issueRefund, new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = 25m });
await Show(issueRefund, new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = 500m });

var missingGrant = policy.Authorize(principal, Grant("GetOrder"), "GetInternalFraudScore", []);
Console.WriteLine($"\nGetInternalFraudScore: {missingGrant.Outcome} — {missingGrant.Reason}");
Console.WriteLine("DeleteCustomer: not registered; the model never receives its definition.");
