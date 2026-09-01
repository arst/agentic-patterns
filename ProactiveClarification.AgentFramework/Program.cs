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

// ── Write the answer back into slot state ────────────────────────────────────
// The step that makes the round trip worth making. One free-text reply covers several questions
// ("Berlin, next Tuesday, 3 nights, max EUR 150"), so a parser splits it per slot and the gate
// merges it. A reply that answers MORE than was asked is kept - the user volunteering a budget
// after the budget question was cut is information, not an attack. What the gate refuses is a
// reply rewriting a slot the request had already settled. Without any of this the host asks, is
// told, and then "assumes" the thing it was just told.
var askedSlots = screened
    .Where(q => q.Allowed)
    .Select(q => q.TargetSlot!)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

if (!string.IsNullOrWhiteSpace(reply))
{
    var parser = new ChatClientAgent(client, name: "ReplyParser",
        instructions: """
                      Split a free-text answer into the slots it answers. Slot names are exactly:
                      destination, checkIn, nights, budget.

                      Return only slots the reply genuinely answers - never guess, and never carry
                      a value over from the question wording. Values as the user gave them.
                      """);

    var parsed = (await parser.RunAsync<ClarificationReply>(
        $"Questions asked:\n{string.Join("\n", allowed)}\n\nUser reply:\n{reply}", options: lowTemp)).Result;

    Console.WriteLine("\n=== Merging the reply into slot state ===");
    foreach (var merged in ClarificationGate.Merge(filled,
                 slots.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), askedSlots,
                 parsed.Answers.Select(a => (a.Slot, a.Value))))
        Console.WriteLine(merged.Merged
            ? $"  {merged.Slot} = {merged.Value}"
            : $"  ignored {merged.Slot} = {merged.Value}  ({merged.IgnoredBecause})");
}

// Whatever is STILL missing after the answer has been merged is assumed, out loud.
var stillMissing = slots.Select(s => s.Name)
    .Where(name => !filled.ContainsKey(name))
    .ToList();

var booker = new ChatClientAgent(client, name: "Booker",
    instructions: """
                  You produce a booking proposal. You will be given the original request, the
                  slots the host has established - from the request and from the clarification
                  round, already merged - and the slots that are still unknown.

                  Treat the established slots as settled, not as suggestions.
                  For every still-unknown slot, pick a sensible default and list it under
                  "Assumptions:" in the form "slot = value (assumed)". Never ask a question:
                  the clarification round is over. Finish with a one-paragraph proposal.
                  """);

var brief = $"""
             Request: {Request}
             Established slots:
             {(filled.Count == 0 ? "  (nothing)" : string.Join("\n", filled.Select(f => $"  {f.Key} = {f.Value}")))}
             Still unknown, assume these: {(stillMissing.Count == 0 ? "(none)" : string.Join(", ", stillMissing))}
             """;

Console.WriteLine($"\n=== Proposal ===\n{await booker.RunAsync(brief, options: lowTemp)}");

internal sealed record ParsedAnswer(string Slot, string Value);
internal sealed record ClarificationReply(ParsedAnswer[] Answers);
internal sealed record FilledSlot(string Slot, string Value);
internal sealed record Triage(FilledSlot[] Filled, string[] Questions);
