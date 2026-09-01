using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiSourceContextFusion.AgentFramework;
using Shared;

// Multi-source context fusion: several systems describe the same customer, they disagree, and
// something has to decide before the model is asked anything.
//
// ContextAssembly answers "what fits in the window". This answers the question that comes first:
// which of these two contradictory values is true. They are different jobs - a budget cannot
// resolve a conflict, and a conflict rule cannot fit a window - and doing them in the wrong order
// gets you a beautifully budgeted context built on the wrong address.

var today = new DateOnly(2026, 9, 1);

Fact[] facts =
[
    new("name", "Ingrid Halvorsen", "crm", Trust.SystemOfRecord, today.AddMonths(-8)),
    new("name", "I. Halvorsen", "support-ticket", Trust.UserStated, today.AddDays(-3)),

    // The one that matters: billing is the system of record, the ticket is what the customer
    // typed yesterday. Recency loses to trust, and the agent is told the customer disagrees.
    new("billing_address", "Storgata 14, 0155 Oslo", "billing", Trust.SystemOfRecord, today.AddMonths(-14)),
    new("billing_address", "Bygdoy alle 3, 0257 Oslo", "support-ticket", Trust.UserStated, today.AddDays(-1)),

    // Same trust tier, so recency decides - and the stale one is still shown.
    new("plan", "Business, 42 seats", "billing", Trust.SystemOfRecord, today.AddDays(-2)),
    new("plan", "Business, 32 seats", "data-warehouse", Trust.SystemOfRecord, today.AddDays(-30)),

    new("churn_risk", "0.71", "model", Trust.Inferred, today),
    new("open_tickets", "2", "support", Trust.SystemOfRecord, today),
    new("preferred_language", "Norwegian", "profile", Trust.UserStated, today.AddYears(-1)),
    new("preferred_language", "Norwegian", "crm", Trust.SystemOfRecord, today.AddMonths(-8))
];

var fused = ContextFusion.Fuse(facts);

Console.WriteLine("=== Fusion ===");
foreach (var resolution in fused)
    Console.WriteLine($"  {resolution.Field}: {resolution.Winner.Value}" +
                      $"  <- {resolution.Winner.Source} ({resolution.Rule})" +
                      (resolution.WasContested
                          ? $"\n      lost: {string.Join("; ", resolution.Losers.Select(l => $"{l.Source} '{l.Value}' ({l.Trust}, {l.AsOf:yyyy-MM-dd})"))}"
                          : ""));

var contested = fused.Where(r => r.WasContested).ToList();
Console.WriteLine($"\n{contested.Count} of {fused.Count} fields were contested.");

// ── The agent gets the resolved view, conflicts included ─────────────────────
var agent = new ChatClientAgent(Settings.ChatClient, name: "AccountAgent",
    instructions: """
                  You brief an account manager from a fused customer record.

                  Fields marked CONTESTED have disagreeing sources. Use the resolved value, name
                  the disagreement explicitly, and say what should be confirmed with the customer.
                  Never silently pick the other value.
                  """);

Console.WriteLine($"\n=== Briefing ===");
Console.WriteLine(await agent.RunAsync(
    $"Customer record:\n{ContextFusion.Render(fused)}\n\n" +
    "Brief me before I call this customer about their renewal.",
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f })));
