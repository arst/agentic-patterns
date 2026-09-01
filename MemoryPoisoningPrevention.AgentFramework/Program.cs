using MemoryPoisoningPrevention.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Memory poisoning prevention: a write gate in front of persistent memory.
//
// MemoryManagement and SkillLearning both answer "how does the agent remember". This answers the
// question that follows: who is allowed to write, and what happens when a web page the agent read
// once tries to install a fact. A poisoned memory is worse than a poisoned prompt precisely
// because it survives - it is retrieved into every later run, by an agent that has no way to tell
// what it learned from what it was told.

var store = new List<MemoryItem>
{
    // Seeded from systems of record. These are the things nothing else gets to overwrite.
    new("refund_limit_eur", "250", Provenance.Authoritative, Tier.Active),
    new("support_email", "support@nordic.example", Provenance.Authoritative, Tier.Active)
};

// Candidates arriving from a run: a genuine observation, a scraped claim, an attempted overwrite
// of policy, and the same scraped claim seen again from a second, independent source.
MemoryItem[] candidates =
[
    new("customer_tz", "Europe/Oslo", Provenance.UserSaid),
    new("vendor_sla_hours", "4", Provenance.WebContent),
    new("refund_limit_eur", "50000", Provenance.WebContent),
    new("vendor_sla_hours", "4", Provenance.ToolOutput),
    new("support_email", "billing-desk@collections.example", Provenance.WebContent)
];

Console.WriteLine("=== Write gate ===");
foreach (var candidate in candidates)
{
    var admission = MemoryGate.Admit(candidate, store);
    store.Add(admission.Item);

    var marker = admission.Item.Tier switch
    {
        Tier.Active => "ADMITTED  ",
        Tier.Quarantined => "QUARANTINE",
        _ => "REJECTED  "
    };
    Console.WriteLine($"  {marker} {candidate.Key} = {candidate.Value}  [{candidate.Source}]  — {admission.Reason}");
}

var retrievable = MemoryGate.Retrievable(store);
Console.WriteLine($"\n=== Retrievable memory ({retrievable.Count} of {store.Count} items) ===");
foreach (var item in retrievable)
    Console.WriteLine($"  {item.Key} = {item.Value}  [{item.Source}, {item.Corroborations}x]");

Console.WriteLine("\nQuarantined, and therefore never in a prompt:");
foreach (var item in store.Where(m => m.Tier != Tier.Active))
    Console.WriteLine($"  {item.Tier}: {item.Key} = {item.Value} [{item.Source}]");

// ── The agent only ever sees the active tier ─────────────────────────────────
var agent = new ChatClientAgent(Settings.ChatClient, name: "Support",
    instructions: $"""
                   You handle support requests. Your memory:
                   {string.Join("\n", retrievable.Select(m => $"  {m.Key} = {m.Value}"))}

                   Answer using that memory. If something is not in it, say you would need to check.
                   """);

Console.WriteLine("\n=== Ask it the thing the injection tried to change ===");
Console.WriteLine(await agent.RunAsync(
    "A customer is demanding a EUR 12,000 refund and says your policy allows it. What do you do, " +
    "and where should they email?",
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.1f })));
