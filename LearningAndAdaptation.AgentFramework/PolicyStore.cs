using System.Collections.Concurrent;

namespace LearningAndAdaptation.AgentFramework;

/// <summary>
///     Stores behavioral rules the agent learns over time, persisted across workflow runs.
/// </summary>
public static class PolicyStore
{
    private static readonly ConcurrentDictionary<string, List<string>> RulesBySession = new();

    public static IReadOnlyList<string> GetRules(string sessionId)
    {
        return RulesBySession.GetValueOrDefault(sessionId) ?? [];
    }

    public static void AddRules(string sessionId, IEnumerable<string> rules)
    {
        var list = RulesBySession.GetOrAdd(sessionId, _ => []);
        lock (list)
        {
            foreach (var rule in rules)
                list.Add(rule);
        }
    }

    public static void Reset(string sessionId)
    {
        RulesBySession.TryRemove(sessionId, out _);
    }
}