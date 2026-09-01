using System.Globalization;

namespace ContrastiveExplanation.AgentFramework;

public sealed record SupportCase(string Id, decimal AccountValueEur, double ChurnRisk, bool Regulated,
    int PriorEscalations);

public enum Route { Standard, Priority, ExecutiveEscalation }

/// The decision itself is a rule, not a model call. That is what makes contrastive explanation
/// checkable: there is a function to re-run.
public static class RoutingPolicy
{
    public const decimal ValueThreshold = 25_000m;
    public const double RiskThreshold = 0.70;

    public static Route Decide(SupportCase c) =>
        c.Regulated || (c.AccountValueEur >= ValueThreshold && c.ChurnRisk >= RiskThreshold)
            ? Route.ExecutiveEscalation
            : c.AccountValueEur >= ValueThreshold || c.ChurnRisk >= RiskThreshold || c.PriorEscalations > 1
                ? Route.Priority
                : Route.Standard;
}

public sealed record Change(string Field, string Value);

public static class Counterfactual
{
    /// Applies the model's proposed minimal change and re-runs the rule.
    ///
    /// This is the step that turns an explanation into a claim with a truth value. "It would have
    /// been Priority if the account were smaller" either flips the decision when you actually
    /// make the account smaller, or it does not - and a plausible-sounding explanation that does
    /// not flip it is exactly the failure this catches. An unverified explanation is a story about
    /// the decision; a verified one is a statement about the rule.
    public static (bool Flipped, Route Actual, SupportCase Modified) Verify(
        SupportCase original, IReadOnlyList<Change> changes, Route alternative)
    {
        var modified = original;
        foreach (var change in changes)
            modified = change.Field.ToLowerInvariant() switch
            {
                "accountvalueeur" when decimal.TryParse(change.Value, CultureInfo.InvariantCulture, out var v) =>
                    modified with { AccountValueEur = v },
                "churnrisk" when double.TryParse(change.Value, CultureInfo.InvariantCulture, out var r) => modified with { ChurnRisk = r },
                "regulated" when bool.TryParse(change.Value, out var b) => modified with { Regulated = b },
                "priorescalations" when int.TryParse(change.Value, CultureInfo.InvariantCulture, out var n) =>
                    modified with { PriorEscalations = n },
                // An unknown field cannot be applied, so the counterfactual cannot be true.
                _ => modified
            };

        var actual = RoutingPolicy.Decide(modified);
        return (actual == alternative, actual, modified);
    }
}
