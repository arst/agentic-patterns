using System.Security.Cryptography;
using AuthenticatedDelegation.AgentFramework;

var now = DateTimeOffset.UtcNow;
var authority = new DelegationAuthority(RandomNumberGenerator.GetBytes(32));
var server = new DelegatedResourceServer(authority, "payments-api");
var grant = authority.Issue(new(
    Id: "grant-42",
    User: "user-7",
    Agent: "purchasing-agent",
    Audience: "payments-api",
    Capabilities: ["payment:create"],
    Resource: "invoice-1042",
    MaxAmount: 100m,
    NotBefore: now.AddMinutes(-1),
    ExpiresAt: now.AddMinutes(5)));

ActionRequest Valid(decimal amount) => new(
    Guid.NewGuid().ToString("N"), "purchasing-agent", "payments-api",
    "payment:create", "invoice-1042", amount, grant);

Console.WriteLine($"EUR 75:  {server.Authorize(Valid(75m), now)}");
Console.WriteLine($"EUR 125: {server.Authorize(Valid(125m), now)}");
Console.WriteLine("\nAttributable audit log:");
foreach (var entry in server.AuditLog)
    Console.WriteLine($"  user={entry.User} agent={entry.Agent} action={entry.Capability} allowed={entry.Allowed} reason={entry.Reason}");
