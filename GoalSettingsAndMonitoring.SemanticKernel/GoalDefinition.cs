namespace GoalSettingsAndMonitoring.SemanticKernel;

public static class GoalDefinition
{
    public static readonly string[] Goals =
    [
        "Code must be syntactically valid C#",
        "Method must include XML documentation comments",
        "Must handle edge cases: null input, empty string, negative numbers",
        "Must include at least 2 example test assertions"
    ];

    public static string GoalsAsText =>
        string.Join("\n", Goals.Select((g, i) => $"  {i + 1}. {g}"));
}