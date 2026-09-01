using ContrastiveExplanation.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Contrastive explanation: not "why did you choose A", but "why A rather than B, and what would
// have had to be different for B".
//
// "Why A" invites a justification, and a model will always produce one - fluent, plausible, and
// unfalsifiable. "Why A rather than B" forces the answer to name the discriminating facts, and
// "what minimal change flips it" forces a claim the host can TEST by re-running the rule. What
// gets shown to the user is only the explanation that survived that test.

var support = new SupportCase("CASE-8891", AccountValueEur: 41_000m, ChurnRisk: 0.82,
    Regulated: false, PriorEscalations: 1);

var decision = RoutingPolicy.Decide(support);
var alternative = Route.Priority; // the route a reviewer would most plausibly have expected

Console.WriteLine($"""
                   Case:      {support.Id}
                   Value:     EUR {support.AccountValueEur:N0}   (threshold {RoutingPolicy.ValueThreshold:N0})
                   Churn:     {support.ChurnRisk:F2}      (threshold {RoutingPolicy.RiskThreshold:F2})
                   Regulated: {support.Regulated}
                   Prior escalations: {support.PriorEscalations}

                   Decision:  {decision}   (contrast: {alternative})
                   """);

var explainer = new ChatClientAgent(Settings.ChatClient, name: "Explainer",
    instructions: $$"""
                    You explain a routing decision contrastively.

                    The rule, in full:
                      ExecutiveEscalation if regulated, OR (value >= {{RoutingPolicy.ValueThreshold}} AND churn >= {{RoutingPolicy.RiskThreshold}})
                      else Priority if value >= {{RoutingPolicy.ValueThreshold}} OR churn >= {{RoutingPolicy.RiskThreshold}} OR priorEscalations > 1
                      else Standard

                    Produce:
                      because: one sentence naming ONLY the facts that discriminate the actual
                               decision from the contrast. Do not list facts that are true of both.
                      changes: the SMALLEST set of field changes that would have produced the
                               contrast instead. Fields: AccountValueEur, ChurnRisk, Regulated,
                               PriorEscalations. Values as plain strings.
                    """);

var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0f });

for (var attempt = 1; attempt <= 2; attempt++)
{
    var explanation = (await explainer.RunAsync<Explanation>(
        $"Case: {support}\nActual decision: {decision}\nContrast: {alternative}", options: precise)).Result;

    var changes = explanation.Changes.Select(c => new Change(c.Field, c.Value)).ToList();
    var (flipped, actual, modified) = Counterfactual.Verify(support, changes, alternative);

    Console.WriteLine($"\n=== Attempt {attempt} ===");
    Console.WriteLine($"  because: {explanation.Because}");
    Console.WriteLine($"  counterfactual: {string.Join(", ", changes.Select(c => $"{c.Field} -> {c.Value}"))}");
    Console.WriteLine($"  re-running the rule on the modified case gives: {actual}");

    if (flipped)
    {
        Console.WriteLine($"""

                           === Verified explanation ===
                           {support.Id} was routed to {decision} rather than {alternative}
                           because {Lede(explanation.Because)}.

                           It would have been {alternative} if {string.Join(" and ",
                               changes.Select(c => $"{c.Field} were {c.Value}"))}
                           (checked: EUR {modified.AccountValueEur:N0}, churn {modified.ChurnRisk:F2},
                           regulated {modified.Regulated}, prior escalations {modified.PriorEscalations}
                           -> {actual}).
                           """);
        return;
    }

    Console.WriteLine($"  REJECTED: the proposed change yields {actual}, not {alternative}. Retrying.");
}

// Two failed attempts is a result, not an error to swallow: the decision stands, unexplained.
Console.WriteLine($"\n=== No verified explanation ===\n{support.Id} -> {decision}. The model could not " +
                  "produce a counterfactual that survives re-running the rule, so none is shown.");

// The template already supplies "because", and models reliably start the clause with it too.
static string Lede(string because) =>
    because.TrimEnd('.') is var trimmed && trimmed.StartsWith("Because ", StringComparison.OrdinalIgnoreCase)
        ? trimmed["Because ".Length..]
        : trimmed;

internal sealed record ProposedChange(string Field, string Value);
internal sealed record Explanation(string Because, ProposedChange[] Changes);
