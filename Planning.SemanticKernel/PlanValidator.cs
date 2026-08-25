using System.Text.RegularExpressions;

namespace Planning.SemanticKernel;

// Duplicated from Planning.AgentFramework/PlanValidator.cs on purpose - the samples stay standalone.

public sealed record PlanValidationError(int StepId, string Message);

public static partial class PlanValidator
{
    [GeneratedRegex(@"\{\{step(\d+)\}\}")] private static partial Regex Placeholder();

    // A step whose tool is a key here must take its argument directly (not via free text, not via
    // a literal) from the output of a preceding step whose tool is the mapped value - closes the
    // gap where a model could skip SelectCheapest/RequestBookingApproval and still pass validation.
    private static readonly Dictionary<string, string> RequiredProducer = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SelectCheapest"] = "GetFlights",
        ["RequestBookingApproval"] = "SelectCheapest",
        ["BookFlight"] = "RequestBookingApproval"
    };

    /// A model-generated plan is untrusted input. Validate it as a whole BEFORE any step runs:
    /// a plan that fails here never touches a tool.
    public static IReadOnlyList<PlanValidationError> Validate(Plan plan, IReadOnlySet<string> allowedTools,
        int maxSteps)
    {
        var errors = new List<PlanValidationError>();
        if (plan.Steps.Count == 0) errors.Add(new PlanValidationError(0, "Plan has no steps."));
        if (plan.Steps.Count > maxSteps)
            errors.Add(new PlanValidationError(0, $"Plan has {plan.Steps.Count} steps; the limit is {maxSteps}."));

        var seen = new HashSet<int>();
        var producedBy = new Dictionary<int, string>();
        foreach (var step in plan.Steps)
        {
            if (!seen.Add(step.Id)) errors.Add(new PlanValidationError(step.Id, "Duplicate step id."));
            if (!allowedTools.Contains(step.Tool))
                errors.Add(new PlanValidationError(step.Id, $"Tool '{step.Tool}' is not allowed."));

            foreach (var value in step.Args.Values)
                foreach (Match match in Placeholder().Matches(value))
                {
                    var referenced = int.Parse(match.Groups[1].Value);
                    if (referenced == step.Id || !seen.Contains(referenced))
                        errors.Add(new PlanValidationError(step.Id,
                            $"Step {step.Id} references step {referenced}, which does not precede it."));
                }

            if (RequiredProducer.TryGetValue(step.Tool, out var requiredProducer) &&
                !step.Args.Values.Any(v => IsExactOutputOf(v, requiredProducer, producedBy)))
                errors.Add(new PlanValidationError(step.Id,
                    $"'{step.Tool}' must take its argument directly from a preceding '{requiredProducer}' " +
                    "step's output (e.g. \"{{stepN}}\"), not a literal value or a different step."));

            producedBy[step.Id] = step.Tool;
        }

        return errors;
    }

    private static bool IsExactOutputOf(string value, string requiredProducer, Dictionary<int, string> producedBy)
    {
        var match = Placeholder().Match(value);
        return match.Success && match.Value == value
                              && producedBy.TryGetValue(int.Parse(match.Groups[1].Value), out var producerTool)
                              && string.Equals(producerTool, requiredProducer, StringComparison.OrdinalIgnoreCase);
    }

    /// Substitutes {{stepN}} from memory and refuses to hand a tool an unresolved placeholder.
    public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, string> args,
        IReadOnlyDictionary<string, string> memory)
    {
        var resolved = args.ToDictionary(kv => kv.Key,
            kv => Placeholder().Replace(kv.Value, m => memory.GetValueOrDefault(m.Groups[1].Value, m.Value)));
        foreach (var (name, value) in resolved)
            if (Placeholder().IsMatch(value))
                throw new InvalidOperationException(
                    $"Argument '{name}' still contains an unresolved placeholder: {value}");
        return resolved;
    }
}
