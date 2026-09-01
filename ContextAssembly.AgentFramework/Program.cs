using ContextAssembly.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Context assembly: the host decides what goes into the window, under a budget, with reasons.
//
// This sits underneath RAG rather than beside it. Retrieval answers "what documents match"; that
// is one source among several - conversation history, long-term memory, tool output, profile -
// and none of them knows about the others or about the budget they are all spending from. Someone
// has to rank across sources and say no. That someone is the host, before the call, not the model
// halfway through it.

const string Question = "The customer is asking why their March invoice is higher. What do I tell them?";

// Candidates as they arrive from every source. Relevance scores come from each source's own
// retriever; the assembler's job is to arbitrate ACROSS them, which no single source can do.
Candidate[] candidates =
[
    new("system", "You are a billing support agent for a Nordic SaaS company.", 1.0, Pinned: true),
    new("user", Question, 1.0, Pinned: true),

    new("account", "Account NORD-2291, plan Business, 42 seats, billed monthly on the 3rd.", 0.91),
    new("billing-db", "March invoice EUR 1,428.00; February invoice EUR 1,092.00.", 0.95),
    new("billing-db", "Seat count rose from 32 to 42 on 11 March (mid-cycle, prorated).", 0.94),

    // Same fact, different system. One of these is pure waste.
    new("crm-notes", "Seat count increased from 32 to 42 on the 11th of March, prorated mid-cycle.", 0.72),

    new("kb", "Proration policy: mid-cycle seat additions are charged pro rata for the remainder " +
              "of the billing period and in full from the next period.", 0.88),
    new("history", "Two weeks ago the customer asked about switching to annual billing.", 0.41),
    new("history", "Last year the customer disputed a charge; resolved as correct, no refund.", 0.35),
    new("kb", "Refund policy: refunds require manager approval above EUR 250.", 0.30),
    new("telemetry", "Login volume up 28% month over month.", 0.12),
    new("marketing", "Q2 campaign: 'Scale with confidence' — 10% off annual upgrades.", 0.05)
];

var context = ContextAssembler.Assemble(candidates, tokenBudget: 120);

Console.WriteLine($"=== Assembled context: {context.Tokens}/{context.Budget} tokens, " +
                  $"{context.Included.Count} of {candidates.Length} candidates ===");
foreach (var item in context.Included)
    Console.WriteLine($"  [{item.Source}{(item.Pinned ? ", pinned" : $", {item.Relevance:F2}")}] {item.Text}");

Console.WriteLine("\n=== Dropped, with reasons ===");
foreach (var (candidate, why) in context.Dropped)
    Console.WriteLine($"  [{candidate.Source}, {candidate.Relevance:F2}] {Truncate(candidate.Text)}\n      {why}");

// ── The call sees exactly what the assembler decided ─────────────────────────
var assembled = string.Join("\n", context.Included
    .Where(c => c.Source != "system" && c.Source != "user")
    .Select(c => $"[{c.Source}] {c.Text}"));

var agent = new ChatClientAgent(Settings.ChatClient, name: "Billing",
    instructions: context.Included.First(c => c.Source == "system").Text +
                  "\n\nAnswer only from the context you are given. If something you would need is " +
                  "not there, say which fact is missing rather than guessing.");

Console.WriteLine($"\n=== Answer ===");
Console.WriteLine(await agent.RunAsync($"Context:\n{assembled}\n\nQuestion: {Question}",
    options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f })));

return;

static string Truncate(string text) => text.Length <= 70 ? text : text[..67] + "...";
