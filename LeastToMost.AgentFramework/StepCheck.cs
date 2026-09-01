using System.Globalization;
using System.Text.RegularExpressions;

namespace LeastToMost.AgentFramework;

public sealed record CheckResult(bool Passed, string Detail);

/// Optional deterministic checks on a subproblem's answer.
///
/// Least-to-most is usually sold on "each step is inspectable", and the sample's own comments made
/// that argument. Inspectable is not validated. Carrying earlier answers forward as *established
/// facts* is a rigid error-propagation channel: a wrong figure in step 2 is not questioned by
/// step 5, it is cited by it, and the chain arrives at a confidently wrong total.
///
/// The real benefit is one step further along: externalising intermediate state means a check
/// CAN be attached where one exists. Most steps here have no verifier - "which billing dates
/// apply" is not mechanically checkable without re-implementing the problem. The total is, so it
/// gets one.
public static partial class StepChecks
{
    [GeneratedRegex(@"(?:EUR|€)\s*([0-9]+(?:[.,][0-9]{1,2})?)|([0-9]+(?:\.[0-9]{1,2})?)\s*(?:EUR|€)")]
    private static partial Regex Money();

    /// The last monetary figure an answer states - by convention the one it concludes with.
    public static decimal? StatedTotal(string answer)
    {
        var matches = Money().Matches(answer);
        if (matches.Count == 0) return null;

        var last = matches[^1];
        var text = (last.Groups[1].Success ? last.Groups[1] : last.Groups[2]).Value.Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// The billing rules, in code. Not a hardcoded expected answer - the same rules the prompt
    /// states, evaluated deterministically, which is the only kind of check worth having.
    public static decimal BillingTotal(DateOnly start, DateOnly upgradeEffective, DateOnly cancelled,
        decimal beforeUpgrade, decimal afterUpgrade)
    {
        var total = 0m;
        for (var charge = start; charge <= cancelled; charge = charge.AddMonths(1))
            total += charge < upgradeEffective ? beforeUpgrade : afterUpgrade;
        return total;
    }

    public static CheckResult AgainstTotal(string answer, decimal expected)
    {
        var stated = StatedTotal(answer);
        if (stated is null) return new CheckResult(false, "the answer states no monetary total");

        return stated == expected
            ? new CheckResult(true, $"EUR {stated:F2} matches the schedule computed by the host")
            : new CheckResult(false,
                $"the answer says EUR {stated:F2}; the host's schedule gives EUR {expected:F2}");
    }
}
