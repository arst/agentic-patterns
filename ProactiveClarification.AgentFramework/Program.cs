using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProactiveClarification.AgentFramework;
using Shared;

// Proactive clarification: the agent asks before it acts - but exactly once, only about things
// it was not told, and never more than the host allows.
//
// The interesting part is not "the model can ask a question". It is the two hard limits the host
// puts around that: a screen that throws out questions the request already answered, and a
// single round, after which anything still missing becomes a stated assumption rather than
// another question. Unbounded clarification is a worse failure than a wrong assumption - it is
// an agent that never starts.

var client = Settings.ChatClient;
var lowTemp = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f });

// The host owns the definition of "enough information to act".
Slot[] slots =
[
    new("destination", ["destination", "city", "where", "location", "country"]),
    new("checkIn", ["check-in", "check in", "date", "when", "arrival", "night of"]),
    new("nights", ["nights", "how long", "duration", "stay length"]),
    new("budget", ["budget", "price", "cost", "per night", "spend", "expensive"])
];

const string Request = "Book me a room next week, somewhere warm, and not too expensive.";
Console.WriteLine($"User: {Request}\n");

var triageAgent = new ChatClientAgent(client, name: "Triage",
    instructions: """
                  You are booking a hotel room. The host requires four things: destination,
                  checkIn, nights, budget.

                  Read the request and report:
                  - filled: the slots the request genuinely pins down, with the value. "somewhere
                    warm" does NOT pin down a destination; "next week" does NOT pin down a date.
                  - questions: one short question per missing slot, naming the slot's subject
                    explicitly ("Which city?", "How many nights?").

                  Never ask about a slot you listed as filled.
                  """);

var triage = (await triageAgent.RunAsync<Triage>(Request, options: lowTemp)).Result;
var filled = triage.Filled.ToDictionary(f => f.Slot, f => f.Value, StringComparer.OrdinalIgnoreCase);

Console.WriteLine("=== Triage ===");
if (filled.Count == 0) Console.WriteLine("  (the request pinned down nothing: 'somewhere warm' is not a destination)");
foreach (var (slot, value) in filled) Console.WriteLine($"  filled: {slot} = {value}");

// ── The gate ─────────────────────────────────────────────────────────────────
var screened = ClarificationGate.Screen(slots, filled.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
    triage.Questions, maxQuestions: 3);

Console.WriteLine();
foreach (var question in screened)
    Console.WriteLine(question.Allowed
        ? $"  ask: {question.Question}"
        : $"  dropped: {question.Question}  ({question.RejectedBecause})");

var allowed = screened.Where(q => q.Allowed).Select(q => q.Question).ToList();

// ── One round, no more ───────────────────────────────────────────────────────
var reply = "";
if (allowed.Count > 0)
{
    Console.WriteLine("\n=== Clarification (one round only) ===");
    foreach (var question in allowed) Console.WriteLine($"  - {question}");
    Console.Write("\nYour answer (one line, or Enter to skip): ");
    reply = Console.ReadLine() ?? ""; // EOF -> no answer -> the run proceeds on assumptions
}

// Whatever is still missing after the single round is assumed, out loud, and the run continues.
var stillMissing = slots.Select(s => s.Name)
    .Where(name => !filled.ContainsKey(name))
    .ToList();

var booker = new ChatClientAgent(client, name: "Booker",
    instructions: """
                  You produce a booking proposal. You will be given the original request, the
                  slots that were pinned down, the user's answer to the clarifying questions (it
                  may be empty), and the slots that are still unknown.

                  For every still-unknown slot, pick a sensible default and list it under
                  "Assumptions:" in the form "slot = value (assumed)". Never ask a question:
                  the clarification round is over. Finish with a one-paragraph proposal.
                  """);

var brief = $"""
             Request: {Request}
             Pinned down: {(filled.Count == 0 ? "(nothing)" : string.Join(", ", filled.Select(f => $"{f.Key}={f.Value}")))}
             Clarifying questions asked: {(allowed.Count == 0 ? "(none)" : string.Join(" | ", allowed))}
             User's answer: {(string.IsNullOrWhiteSpace(reply) ? "(none given)" : reply)}
             Still unknown before your assumptions: {(stillMissing.Count == 0 ? "(none)" : string.Join(", ", stillMissing))}
             """;

Console.WriteLine($"\n=== Proposal ===\n{await booker.RunAsync(brief, options: lowTemp)}");

internal sealed record FilledSlot(string Slot, string Value);
internal sealed record Triage(FilledSlot[] Filled, string[] Questions);
