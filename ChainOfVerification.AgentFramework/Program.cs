using ChainOfVerification.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Chain of Verification: draft → plan checks → answer each check in isolation → revise.
//
// The whole point is the isolation in step 3. Asking the same context "are you sure?" gets you
// the same answer with more confidence; asking a fresh model a narrow factual question, with the
// draft nowhere in sight, is a genuinely independent measurement.

var client = Settings.ChatClient;
var lowTemp = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.2f });

const string Question =
    "Name four European cities that began as Roman settlements. For each, give the Roman name " +
    "and the founding year. Two or three sentences total per city, no hedging.";

// ── 1. Draft ─────────────────────────────────────────────────────────────────
// Deliberately the kind of question that invites confident, specific, wrong details.
var drafter = new ChatClientAgent(client, name: "Drafter",
    instructions: "You answer factual questions directly and specifically. Never hedge.");

var draft = (await drafter.RunAsync(Question, options: lowTemp)).Text;
Console.WriteLine($"=== Draft ===\n{draft}\n");

// ── 2. Plan the checks ───────────────────────────────────────────────────────
var planner = new ChatClientAgent(client, name: "Planner",
    instructions: """
                  Extract the individual factual claims from a draft answer, then write one
                  verification question per claim.

                  For each claim set:
                  - text: the claim in one sentence.
                  - value: ONLY the specific detail that could be wrong (a year, a Roman name, a number).
                  - question: a question that checks the claim WITHOUT stating the value. Ask
                    "In what year was X founded?", never "Was X founded in 38 BC?".

                  Return at most 8 claims.
                  """);

var plan = (await planner.RunAsync<VerificationPlan>(
    $"Draft answer to verify:\n{draft}", options: lowTemp)).Result;

var checks = new List<(Claim Claim, string Question)>();
foreach (var item in plan.Claims)
{
    var claim = new Claim(item.Id, item.Text, item.Value);
    var errors = VerificationGate.Validate(claim, item.Question);
    if (errors.Count > 0)
    {
        Console.WriteLine($"[gate] claim {claim.Id} question rejected: {string.Join(" ", errors)}");
        continue;
    }

    checks.Add((claim, item.Question));
}

Console.WriteLine($"\n=== {checks.Count} verification questions passed the gate ===");
foreach (var (claim, question) in checks)
    Console.WriteLine($"  [{claim.Id}] {question}   (draft says: {claim.Value})");

// ── 3. Answer each check in isolation ────────────────────────────────────────
// A fresh stateless agent, one question per run, no session, no draft in context.
// This is the structural difference from a self-critique loop.
var verifier = new ChatClientAgent(client, name: "Verifier",
    instructions: "Answer the single factual question as precisely as you can. If you are not " +
                  "confident, say so explicitly. Do not speculate about why you are being asked.");

var answers = await Task.WhenAll(checks.Select(async check =>
{
    var answer = (await verifier.RunAsync(check.Question, options: lowTemp)).Text;
    return (check.Claim, check.Question, Answer: answer);
}));

Console.WriteLine("\n=== Independent answers ===");
foreach (var (claim, question, answer) in answers)
    Console.WriteLine($"  [{claim.Id}] {question}\n        → {answer.ReplaceLineEndings(" ")}\n");

// ── 4. Revise ────────────────────────────────────────────────────────────────
// The reviser sees the draft and the independent answers side by side, and is told which one
// wins when they disagree. Without that instruction the model tends to defend its own draft.
var reviser = new ChatClientAgent(client, name: "Reviser",
    instructions: """
                  You are given a draft answer and a set of independently verified facts.

                  Where the verification disagrees with the draft, the verification wins: correct
                  the draft. Where verification was uncertain, drop the claim or mark it as
                  uncertain rather than keeping the confident version. Do not add new claims.

                  Output the corrected answer, then a short "Changes:" list.
                  """);

var evidence = string.Join("\n", answers.Select(a => $"Q: {a.Question}\nA: {a.Answer}"));
var final = await reviser.RunAsync(
    $"Original question:\n{Question}\n\nDraft:\n{draft}\n\nVerified facts:\n{evidence}",
    options: lowTemp);

Console.WriteLine($"=== Verified answer ===\n{final}");

// Structured-output shape for the planning call.
internal sealed record PlannedClaim(int Id, string Text, string Value, string Question);
internal sealed record VerificationPlan(PlannedClaim[] Claims);
