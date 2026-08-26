using Microsoft.Extensions.AI;
using ToolAuthorization.AgentFramework;

var principal = new RunPrincipal("CUSTOMER-100", "TENANT-EU");
var orderOwners = new Dictionary<string, RunPrincipal>(StringComparer.OrdinalIgnoreCase)
{
    ["ORD-100"] = principal,
    ["ORD-999"] = new("CUSTOMER-999", "TENANT-EU")
};
var policy = new ToolAuthorizationPolicy(orderOwners);

ToolCapability Grant(string tool, decimal? maximumAmount = null, bool oneTime = false) => new(
    principal.SubjectId, principal.TenantId, tool, new Dictionary<string, string>(), maximumAmount,
    DateTimeOffset.UtcNow.AddMinutes(5), Guid.NewGuid().ToString("N"), oneTime);

var getOrderFunction = AIFunctionFactory.Create(
    (string orderId) => $"{orderId}: paid, awaiting shipment", "GetOrder");
var refundFunction = AIFunctionFactory.Create(
    (string orderId, decimal amount) => $"Refunded €{amount:F2} for {orderId}", "IssueRefund");

var getOrder = new AuthorizedAIFunction(getOrderFunction, principal, Grant("GetOrder"), policy);
var issueRefund = new AuthorizedAIFunction(refundFunction, principal, Grant("IssueRefund", maximumAmount: 50m), policy);

async Task Show(AIFunction tool, AIFunctionArguments arguments)
{
    Console.WriteLine($"\n{tool.Name}({string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}"))})");
    try
    {
        Console.WriteLine($"  result: {await tool.InvokeAsync(arguments)}");
    }
    catch (ToolAuthorizationException ex)
    {
        // The model never sees any of this: a refusal is a host event, not a tool result.
        if (ex.Decision.PendingApproval is not { } pending)
        {
            Console.WriteLine($"  refused: {ex.Decision.Reason}");
            return;
        }
        await Escalate(pending);
    }
}

async Task Escalate(PendingApproval pending)
{
    Console.WriteLine($"  approval required: {pending.Reason}");
    Console.WriteLine("  → sent to the approver's channel, not returned to the model:");
    Console.WriteLine($"    {pending.ToolName}({string.Join(", ", pending.Arguments.Select(a => $"{a.Key}={a.Value}"))})");

    // ponytail: the approver's answer is a constant so the sample runs with no TTY and no
    // credentials. Upgrade path: await a durable approval record (see DurableHumanInTheLoop) and
    // resume from it — never Console.ReadLine, which would hang an unattended run.
    var approverApproved = true;
    if (!approverApproved)
    {
        Console.WriteLine("  approver declined — the tool is never invoked.");
        return;
    }

    // The approver issues a *new* capability, sized to this one request and single-use. The
    // original capability is never widened, and the model had no part in producing this grant.
    // The sample has exactly one escalating tool, so the inner function is named directly.
    var approved = new AuthorizedAIFunction(refundFunction, principal,
        Grant(pending.ToolName, maximumAmount: 500m, oneTime: true), policy);
    Console.WriteLine("  approver granted a single-use capability for this exact request.");
    Console.WriteLine($"  result: {await approved.InvokeAsync(new AIFunctionArguments(pending.Arguments.ToDictionary()))}");
}

Console.WriteLine("=== Capability-scoped tools ===");
await Show(getOrder, new AIFunctionArguments { ["orderId"] = "ORD-100" });
await Show(getOrder, new AIFunctionArguments { ["orderId"] = "ORD-999" });
await Show(issueRefund, new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = 25m });
await Show(issueRefund, new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = 500m });

Console.WriteLine("\n=== One-time capability: reserve, then commit ===");
var oneTimeGetOrder = new AuthorizedAIFunction(getOrderFunction, principal, Grant("GetOrder", oneTime: true), policy);
await Show(oneTimeGetOrder, new AIFunctionArguments { ["orderId"] = "ORD-100" }); // reserved, then committed
await Show(oneTimeGetOrder, new AIFunctionArguments { ["orderId"] = "ORD-100" }); // refused: already consumed

var missingGrant = policy.Authorize(principal, Grant("GetOrder"), "GetInternalFraudScore", []);
Console.WriteLine($"\nGetInternalFraudScore: {missingGrant.Outcome} — {missingGrant.Reason}");
Console.WriteLine("DeleteCustomer: not registered; the model never receives its definition.");
