using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;
using StateMachineAgent.AgentFramework;

// A state machine the HOST owns, with an LLM filling in the judgement at each state.
//
// Compare with an agent loop: there, the model decides what happens next and the host hopes the
// prompt held. Here the reachable next steps are a C# table. The model is asked one bounded
// question per state - "is this expense routine or does it need approval?" - and the host maps
// its answer onto a transition. An answer outside the menu is an exception, not a new branch.
//
// This is what regulated workflows actually need: you can print the graph, prove Execute is
// unreachable without Approval, and bound every loop.

var client = Settings.ChatClient;
var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0f });

var claim = new ExpenseClaim("EXP-4471", "Client dinner, 6 people, Berlin", 412.80m, HasReceipt: true,
    CostCentre: "");

var caseWorker = new ChatClientAgent(client, name: "CaseWorker",
    instructions: """
                  You are one step in an expense-approval workflow. You will be given the claim,
                  the current step, and the exact list of decisions allowed at this step.

                  Answer with one decision from that list and one sentence of reasoning. Never
                  invent a decision, never describe the next step - the workflow owns that.

                  Policy: claims over EUR 250, or missing a receipt, or missing a cost centre are
                  not routine. A claim with no cost centre is insufficient at intake.
                  """);

var state = State.Intake;
var budget = new VisitBudget(perState: 3);
var log = new List<string>();

while (!ExpenseMachine.IsTerminal(state))
{
    if (!budget.TryVisit(state))
    {
        Console.WriteLine($"\n[budget] {state} visited {budget.Count(state)} times; stopping.");
        state = State.Rejected;
        break;
    }

    // Side effects are the HOST's and run on ENTERING a state, before the model is asked
    // anything - never triggered by the model mentioning them. NeedInfo means "go and get the
    // missing field", so the field is filled here; the model is then asked whether what it now
    // has is sufficient. Asking first and fetching afterwards would put the model in a state it
    // can never leave.
    switch (state)
    {
        case State.NeedInfo:
            claim = claim with { CostCentre = "CC-DE-142" };
            Console.WriteLine($"        [effect] cost centre {claim.CostCentre} retrieved for {claim.Id}");
            break;
        case State.Execute:
            Console.WriteLine($"        [effect] reimbursement queued for {claim.Id}");
            break;
    }

    var allowed = ExpenseMachine.Allowed(state);
    var prompt = $"""
                  Claim: {claim}
                  Facts gathered so far:
                  {(log.Count == 0 ? "  (none)" : string.Join("\n", log.Select(l => "  " + l)))}

                  Current step: {state}
                  What this step decides: {StepBrief(state)}
                  Allowed decisions: {string.Join(", ", allowed)}
                  """;

    var verdict = (await caseWorker.RunAsync<Verdict>(prompt, options: precise)).Result;

    // The model's answer is untrusted input: parse it against the menu before it can move anything.
    if (!Enum.TryParse<Decision>(verdict.Decision, ignoreCase: true, out var decision) ||
        !allowed.Contains(decision))
    {
        Console.WriteLine($"[{state}] rejected off-menu decision '{verdict.Decision}'; treating as Failed.");
        decision = allowed.Contains(Decision.Failed) ? Decision.Failed : allowed[^1];
    }

    var next = ExpenseMachine.Next(state, decision);
    Console.WriteLine($"[{state}] --{decision}--> {next}   ({verdict.Reason})");
    log.Add($"{state}: {decision} - {verdict.Reason}");

    state = next;
}

Console.WriteLine($"\n=== {state} ===");
foreach (var entry in log) Console.WriteLine("  " + entry);

// The state name alone does not tell the model what it is being asked. Without this, NeedInfo
// reads the "Insufficient" entry still sitting in the fact log and concludes the claim is
// doomed - rejecting a claim whose gap the host just closed, intermittently, at temperature 0.
// Naming the question is the host's job for the same reason the menu is: the model supplies
// judgement inside a step, so the step has to be legible.
static string StepBrief(State state) => state switch
{
    State.Intake => "Does the claim, AS SHOWN ABOVE, have everything needed to proceed? Judge the "
                    + "claim as it stands now, not as earlier entries in the fact log described it.",
    State.NeedInfo => "The missing information has just been retrieved and the claim above already "
                      + "reflects it. Sufficient means the gap is now closed. Failed is only for a "
                      + "claim that genuinely cannot be completed at all.",
    State.Classify => "Is this claim routine, or does policy require approval?",
    State.Approval => "Approve or reject the claim on the merits.",
    State.Plan => "Can the reimbursement be prepared from what is known? Ok unless something blocks it.",
    State.Execute => "The reimbursement has been queued. Ok unless the effect above failed.",
    State.Verify => "Does the completed claim satisfy policy? Failed sends it back to Plan.",
    _ => "Decide."
};

internal sealed record ExpenseClaim(string Id, string Description, decimal AmountEur, bool HasReceipt,
    string CostCentre)
{
    public override string ToString() =>
        $"{Id} | {Description} | EUR {AmountEur:F2} | receipt: {(HasReceipt ? "yes" : "no")} | " +
        $"cost centre: {(string.IsNullOrEmpty(CostCentre) ? "MISSING" : CostCentre)}";
}

internal sealed record Verdict(string Decision, string Reason);
