using System.Collections.Concurrent;

namespace LearningAndAdaptation.SemanticKernel;

/// <summary>
/// Stores behavioral rules the agent learns over time.
/// Rules are plain-language instructions injected into every subsequent prompt,
/// e.g. "Be more concise", "Avoid bullet lists when the answer is short".
/// </summary>
public static class PolicyStore
{
    private static readonly ConcurrentDictionary<string, List<string>> RulesBySession = new();

    public static IReadOnlyList<string> GetRules(string sessionId) =>
        RulesBySession.GetValueOrDefault(sessionId) ?? [];

    public static void AddRule(string sessionId, string rule)
    {
        var rules = RulesBySession.GetOrAdd(sessionId, _ => []);
        lock (rules) rules.Add(rule);
    }

    public static void Reset(string sessionId) =>
        RulesBySession.TryRemove(sessionId, out _);
}
