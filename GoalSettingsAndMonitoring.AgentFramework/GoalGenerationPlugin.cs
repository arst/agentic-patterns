using System.ComponentModel;

namespace GoalSettingsAndMonitoring.AgentFramework;

public class CodeGenerationPlugin
{
    [Description(
        "Evaluate the generated code against the defined goals. " +
        "Returns a JSON object with 'allGoalsMet' (bool) and 'feedback' (string). " +
        "Call this AFTER generating or refining code.")]
    public static Task<string> EvaluateGoals(string code)
    {
        // In production: use a separate LLM call, run unit tests, or compile the code.
        // Here we do simple deterministic checks to demonstrate the pattern.
        var unmet = new List<string>();

        if (!code.Contains("///"))
            unmet.Add("Missing XML documentation comments");
        if (!code.Contains("null"))
            unmet.Add("No null handling detected");
        if (!code.Contains("Assert") && !code.Contains("assert"))
            unmet.Add("No test assertions found");
        if (!code.Contains("throw") && !code.Contains("if ("))
            unmet.Add("No edge case handling detected");

        var allMet = unmet.Count == 0;

        var feedback = allMet
            ? "All goals met. Code is ready."
            : $"Unmet goals:\n{string.Join("\n", unmet.Select(u => $"- {u}"))}";

        Console.WriteLine($"  [GoalCheck] {(allMet ? "ALL GOALS MET" : $"{unmet.Count} goal(s) unmet")}");

        return Task.FromResult(
            $$"""{ "allGoalsMet": {{allMet.ToString().ToLower()}}, "feedback": "{{feedback}}" }""");
    }
}