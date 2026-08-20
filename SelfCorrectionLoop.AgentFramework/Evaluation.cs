namespace SelfCorrectionLoop.AgentFramework;

public sealed record CriterionResult(string Name, bool Passed, string Feedback);

public sealed record Evaluation(
    bool Approved,
    double Score,
    IReadOnlyList<CriterionResult> Criteria,
    string Feedback);

public static class HostEvaluation
{
    public static Evaluation Apply(string draft, Evaluation modelEvaluation, int characterLimit,
        string requiredProductName, IReadOnlyList<string> forbiddenTerms)
    {
        if (characterLimit <= 0 || string.IsNullOrWhiteSpace(requiredProductName))
            throw new ArgumentException("A positive character limit and product name are required.");
        forbiddenTerms ??= [];
        var deterministic = new[]
        {
            new CriterionResult("Character limit", draft.Length <= characterLimit,
                $"{draft.Length}/{characterLimit} characters."),
            new CriterionResult("Required product name",
                draft.Contains(requiredProductName, StringComparison.OrdinalIgnoreCase),
                $"Must contain '{requiredProductName}'."),
            new CriterionResult("Forbidden terms",
                !forbiddenTerms.Any(term => draft.Contains(term, StringComparison.OrdinalIgnoreCase)),
                forbiddenTerms.Count == 0 ? "No forbidden terms configured." :
                    $"Must not contain: {string.Join(", ", forbiddenTerms)}.")
        };
        var criteria = (modelEvaluation.Criteria ?? []).Concat(deterministic).ToArray();
        return modelEvaluation with
        {
            Approved = modelEvaluation.Approved && deterministic.All(c => c.Passed),
            Score = Math.Clamp(modelEvaluation.Score, 0, 1),
            Criteria = criteria,
            Feedback = string.Join(" ", new[] { modelEvaluation.Feedback }
                .Concat(deterministic.Where(c => !c.Passed).Select(c => $"Host: {c.Feedback}")))
        };
    }
}
