using AgentCommunicationFaultTolerance.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Fault tolerance for agent-to-agent messaging: ids, retry, dedup, dead-letters, reconciliation.
//
// Once agents talk over a network instead of a method call, every message has three outcomes, not
// two: arrived, lost, and "arrived but the acknowledgement was lost". IdempotentToolCalls solves
// the third one for a tool the agent calls; this solves it for a message the agent sends to
// another agent, where the retry and the effect are on opposite sides of the wire.

var client = Settings.ChatClient;

// The receiving agent's actual work: analysing a shipment note. Expensive enough that doing it
// twice matters, which is what makes dedup worth its bookkeeping.
var analyst = new ChatClientAgent(client, name: "Analyst",
    instructions: "Given a shipment note, reply with one sentence: the risk to the delivery date.");

var effectLog = new List<string>();
string Effect(Message m)
{
    // Synchronous by design: the dedup record and the effect must not be separable by an await,
    // or two duplicates can both pass the check before either writes.
    var reply = analyst.RunAsync(m.Body,
        options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f }))
        .GetAwaiter().GetResult().Text;
    effectLog.Add(m.Id);
    Console.WriteLine($"    [effect ran] {m.Id} attempt {m.Attempt}: {reply.ReplaceLineEndings(" ").Trim()}");
    return reply;
}

// Seeded so the run is reproducible: this seed loses some messages and duplicates others.
// Seed 11 exercises all four mechanisms: a retry, an absorbed duplicate, and one message
// that never gets through.
var transport = new FlakyTransport(seed: 11, lossRate: 0.45, duplicateRate: 0.35);
var inbox = new Inbox();
var channel = new ReliableChannel(transport, inbox, maxAttempts: 4);

Message[] outbound =
[
    new("MSG-1", "Dispatcher", "Analyst", "Shipment SH-771: customs hold in Rotterdam, 2 days."),
    new("MSG-2", "Dispatcher", "Analyst", "Shipment SH-772: carrier strike announced for Thursday."),
    new("MSG-3", "Dispatcher", "Analyst", "Shipment SH-773: cold chain sensor offline since 04:00."),
    new("MSG-4", "Dispatcher", "Analyst", "Shipment SH-774: on schedule, no exceptions.")
];

Console.WriteLine("=== Sending over a transport that loses 45% and duplicates 35% ===");
foreach (var message in outbound)
{
    Console.WriteLine($"\n  {message.Id} -> {message.To}");
    var delivery = await channel.SendAsync(message, Effect);

    Console.WriteLine(delivery.Delivered
        ? $"    delivered on attempt {delivery.Attempts}{(delivery.Duplicate ? " (replayed from the inbox, effect NOT re-run)" : "")}"
        : $"    dead-lettered after {delivery.Attempts} attempts: {delivery.Error}");
}

// ── The third outcome: delivered, but the sender never learned it ────────────
// This is the case that forces the whole design. The sender cannot tell "lost" from
// "arrived, ack lost", so it resends - and the receiver must make that a no-op.
Console.WriteLine("\n=== Resending MSG-2, as a sender that lost the acknowledgement would ===");
var resend = await channel.SendAsync(outbound[1], Effect);
Console.WriteLine(resend.Duplicate
    ? "  replayed from the inbox: the stored result came back and the analysis did NOT run again"
    : "  handled as new — this would be a dedup failure");

// ── Reconciliation ───────────────────────────────────────────────────────────
var missing = ReliableChannel.Reconcile(outbound, inbox);

Console.WriteLine($"\n=== Reconciliation ===");
Console.WriteLine($"  sent: {outbound.Length}   handled by receiver: {inbox.Handled.Count}   " +
                  $"effects actually run: {effectLog.Count}   dead-lettered: {channel.DeadLetters.Count}   " +
                  $"duplicates absorbed: {channel.DuplicatesAbsorbed}");
Console.WriteLine(missing.Count == 0
    ? "  no gap: every sent message is accounted for on the receiving side."
    : $"  gap: {string.Join(", ", missing)} never reached the receiver — requeue or escalate.");

Console.WriteLine($"\nEffects ran {effectLog.Count} time(s) for {inbox.Handled.Count} distinct message(s); " +
                  "duplicates cost a transport round trip, never a second analysis.");
