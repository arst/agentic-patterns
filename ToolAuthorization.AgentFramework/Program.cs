using System.Globalization;
using Microsoft.Extensions.AI;
using ToolAuthorization.AgentFramework;

var principal = new RunPrincipal("CUSTOMER-100", "TENANT-EU");
var orderOwners = new Dictionary<string, RunPrincipal>(StringComparer.OrdinalIgnoreCase)
{
    ["ORD-100"] = principal,
    ["ORD-999"] = new("CUSTOMER-999", "TENANT-EU")
};
var policy = new ToolAuthorizationPolicy(orderOwners);

// The fake half of the approval boundary, behind the interface a real host implements. Declared
// as IApprover so the swap is a one-line change and the demo answer is impossible to mistake for
// a policy decision.
IApprover approver = new DemoApprover(alwaysApprove: true);

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
        // The model never sees any of this — because this host invokes the function directly.
        // Throwing alone does not guarantee it: under FunctionInvokingChatClient the framework
        // catches a function exception and feeds the model a generic error, discarding the
        // PendingApproval. A host on that path must intercept before the loop does.
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

    if (!await approver.ApproveAsync(pending))
    {
        Console.WriteLine("  approver declined — the tool is never invoked.");
        return;
    }

    // The approver issues a *new* capability, single-use and sized to this one request — the
    // ceiling is read off the snapshot the approver saw, not hard-coded, so it stays correct when
    // the probe changes. The original capability is never widened, and the model had no part in
    // producing this grant. The sample has exactly one escalating tool, so the inner function is
    // named directly.
    var requested = Convert.ToDecimal(pending.Arguments["amount"], CultureInfo.InvariantCulture);
    var approved = new AuthorizedAIFunction(refundFunction, principal,
        Grant(pending.ToolName, maximumAmount: requested, oneTime: true), policy);
    Console.WriteLine($"  approver granted a single-use capability capped at €{requested:F2}.");
    try
    {
        Console.WriteLine($"  result: {await approved.InvokeAsync(new AIFunctionArguments(pending.Arguments.ToDictionary()))}");
    }
    catch (ToolAuthorizationException ex)
    {
        // The approver's own grant is enforced too, and the sample must run unattended to the end.
        Console.WriteLine($"  approved call still refused: {ex.Decision.Reason}");
    }
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
