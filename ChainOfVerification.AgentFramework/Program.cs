using ChainOfVerification.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Chain of Verification: draft → plan checks → answer each check blind → revise.
//
// The whole point is the isolation in step 3. Asking the same context "are you sure?" gets you the
// same answer with more confidence; asking a fresh run a narrow factual question, with the draft
// nowhere in sight, removes the anchor.
//
// Be precise about what that buys, because it is easy to oversell. This is INDEPENDENT CONTEXT,
// not independent evidence. The checker is the same deployment with the same weights and the same
// training data, so a misconception the draft has, the check can have too - and on questions like
// Roman founding dates that is not a remote possibility. What you get is a blind cross-check:
// strong evidence when it disagrees, weak evidence when it agrees. Real independence needs a
// different source - retrieval, a tool, a second model - which is what **AgenticRAG** brings.

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

Console.WriteLine($"\n=== {checks.Count} of {plan.Claims.Length} verification questions passed the gate ===");
foreach (var (claim, question) in checks)
    Console.WriteLine($"  [{claim.Id}] {question}   (draft says: {claim.Value})");

// ── 3. Answer each check blind ───────────────────────────────────────────────
// A fresh stateless agent, one question per run, no session, no draft in context.
// This is the structural difference from a self-critique loop.
var verifier = new ChatClientAgent(client, name: "Verifier",
    instructions: "Answer the single factual question as precisely as you can. Begin your reply " +
                  "with CONFIDENT: or UNCERTAIN: — uncertainty is a useful answer and a guess " +
                  "dressed as a fact is not. Do not speculate about why you are being asked.");

var answers = await Task.WhenAll(checks.Select(async check =>
{
    var answer = (await verifier.RunAsync(check.Question, options: lowTemp)).Text;
    return (check.Claim, check.Question, Answer: answer);
}));

Console.WriteLine("\n=== Blind cross-checks ===");
foreach (var (claim, question, answer) in answers)
    Console.WriteLine($"  [{claim.Id}] {question}\n        → {answer.ReplaceLineEndings(" ")}\n");

// ── 4. Revise ────────────────────────────────────────────────────────────────
// The reviser sees the draft and the independent answers side by side, and is told which one
// wins when they disagree. Without that instruction the model tends to defend its own draft.
var reviser = new ChatClientAgent(client, name: "Reviser",
    instructions: """
                  You are given a draft answer and a set of blind cross-checks: the same model
                  answering each factual question with the draft out of sight.

                  A cross-check is not an authority. Resolve each disagreement into one of three
                  outcomes, and never silently keep the draft:

                    - check CONFIDENT and disagrees  -> correct the draft to the check.
                    - check UNCERTAIN and disagrees  -> mark the claim contested: state both
                      values and that they could not be settled. Do not pick one.
                    - check agrees                   -> leave the claim as it is. Agreement between
                      a model and itself is weak evidence, so do not upgrade the wording.

                  Do not add new claims.

                  Output the corrected answer, then "Changes:" listing corrections and contested
                  claims separately.
                  """);

var evidence = string.Join("\n", answers.Select(a => $"Q: {a.Question}\nA: {a.Answer}"));
var final = await reviser.RunAsync(
    $"Original question:\n{Question}\n\nDraft:\n{draft}\n\nBlind cross-checks:\n{evidence}",
    options: lowTemp);

Console.WriteLine($"=== Cross-checked answer ===\n{final}");

// Coverage, stated rather than implied. The planner is capped at 8 claims and the gate drops
// leading questions, so some of the draft's specifics may never have been checked at all -
// calling the result "verified" without saying which claims that covers is the quiet overclaim
// this pattern invites.
const int PlannerCap = 8;
var dropped = plan.Claims.Length - checks.Count;

Console.WriteLine($"""

                   === Coverage ===
                     claims extracted:  {plan.Claims.Length}
                     cross-checked:     {checks.Count}
                     never checked:     {dropped}  (questions the gate refused as leading)
                   """);

// Name exactly which of the two gaps applies. "Partially checked" when nothing was skipped is as
// misleading as "verified" when something was.
Console.WriteLine(
    dropped > 0
        ? $"\n{dropped} claim(s) were never checked, and carry the draft's confidence and nothing more."
        : plan.Claims.Length >= PlannerCap
            ? $"\nEvery extracted claim was cross-checked — but the planner stops at {PlannerCap} and "
              + "returned exactly that many, so a longer draft may hold specifics it never enumerated."
            : "\nEvery claim in the draft was extracted and cross-checked.");

Console.WriteLine("Cross-checked is not verified: the checker shares the drafter's weights, so "
                  + "agreement rules out anchoring on the draft, not a shared misconception.");

// Structured-output shape for the planning call.
internal sealed record PlannedClaim(int Id, string Text, string Value, string Question);
internal sealed record VerificationPlan(PlannedClaim[] Claims);
