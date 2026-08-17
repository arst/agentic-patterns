using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace LearningAndAdaptation.SemanticKernel;

/// <summary>
///     Tools the agent uses to update its own behavioral policy.
///     These are called by the agent itself during self-critique, not by the user.
/// </summary>
public sealed class AdaptationTools(string sessionId)
{
    [KernelFunction]
    [Description(
        "Record a new behavioral rule the agent has learned. Call this after self-critiquing a response and identifying a concrete improvement.")]
    public string LearnRule(
        [Description(
            "A short, actionable behavioral rule to follow in future responses, e.g. 'Keep answers under 5 sentences' or 'Avoid nested bullet lists'")]
        string rule)
    {
        PolicyStore.AddRule(sessionId, rule);
        return $"Rule learned: \"{rule}\"";
    }

    [KernelFunction]
    [Description("Retrieve all behavioral rules learned so far in this session.")]
    public string GetLearnedRules()
    {
        var rules = PolicyStore.GetRules(sessionId);
        return rules.Count == 0
            ? "No rules learned yet."
            : string.Join("\n", rules.Select((r, i) => $"{i + 1}. {r}"));
    }

    [KernelFunction]
    [Description("Reset all learned rules for a session (start fresh).")]
    public string ResetPolicy()
    {
        PolicyStore.Reset(sessionId);
        return "Policy reset.";
    }
}