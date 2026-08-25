using System.Text.RegularExpressions;

namespace Planning.AgentFramework;

public sealed record PlanValidationError(int StepId, string Message);

public static partial class PlanValidator
{
    [GeneratedRegex(@"\{\{step(\d+)\}\}")] private static partial Regex Placeholder();

    // A declarative contract per tool: the exact parameter set it accepts, and - where
    // applicable - which single parameter must carry the {{stepN}} output of a specific
    // preceding tool. Checked by parameter NAME, not by scanning all argument values, and the
    // parameter set is closed (extra/missing keys are rejected) so a decoy key can't smuggle a
    // fabricated value past the real parameter the tool actually binds.
    private sealed record ToolContract(IReadOnlySet<string> Parameters, string? ProducerParameter,
        string? RequiredProducerTool);

    private static readonly Dictionary<string, ToolContract> Contracts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GetFlights"] = new(new HashSet<string> { "from", "to", "date" }, null, null),
        ["SelectCheapest"] = new(new HashSet<string> { "flights" }, "flights", "GetFlights"),
        ["RequestBookingApproval"] = new(new HashSet<string> { "flight" }, "flight", "SelectCheapest"),
        ["BookFlight"] = new(new HashSet<string> { "approvedFlight" }, "approvedFlight", "RequestBookingApproval"),
        ["DraftEmail"] = new(new HashSet<string> { "confirmation" }, "confirmation", "BookFlight")
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

            if (Contracts.TryGetValue(step.Tool, out var contract))
            {
                if (!new HashSet<string>(step.Args.Keys).SetEquals(contract.Parameters))
                    errors.Add(new PlanValidationError(step.Id,
                        $"'{step.Tool}' expects exactly the arguments [{string.Join(", ", contract.Parameters)}], " +
                        $"got [{string.Join(", ", step.Args.Keys)}]."));

                if (contract.ProducerParameter is { } producerParameter &&
                    (!step.Args.TryGetValue(producerParameter, out var producerValue) ||
                     !IsExactOutputOf(producerValue, contract.RequiredProducerTool!, producedBy)))
                    errors.Add(new PlanValidationError(step.Id,
                        $"'{step.Tool}' must take its '{producerParameter}' argument directly from a preceding " +
                        $"'{contract.RequiredProducerTool}' step's output (e.g. \"{{{{stepN}}}}\"), not a " +
                        "literal value, a different parameter, or a different step."));
            }

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
