using System.Collections.Frozen;

namespace ToolAuthorization.AgentFramework;

public enum AuthorizationOutcome { Allowed, Denied, ApprovalRequired }

/// <summary>
/// A request for a human decision. It travels on the host's approval channel, never back to the
/// model as tool output. <see cref="Arguments"/> is a snapshot: the caller's live argument
/// dictionary stays mutable, so an approver must see the values that were actually judged.
/// </summary>
public sealed record PendingApproval
{
    public PendingApproval(string toolName, IEnumerable<KeyValuePair<string, object?>> arguments, string reason)
    {
        ToolName = toolName;
        Arguments = arguments.ToFrozenDictionary(StringComparer.Ordinal);
        Reason = reason;
    }

    public string ToolName { get; }
    public IReadOnlyDictionary<string, object?> Arguments { get; }
    public string Reason { get; }
}

public sealed record AuthorizationDecision(AuthorizationOutcome Outcome, string Reason)
{
    /// <summary>Set only when <see cref="Outcome"/> is <see cref="AuthorizationOutcome.ApprovalRequired"/>.</summary>
    public PendingApproval? PendingApproval { get; init; }

    public static AuthorizationDecision Allow() => new(AuthorizationOutcome.Allowed, "Capability permits this invocation.");
    public static AuthorizationDecision Deny(string reason) => new(AuthorizationOutcome.Denied, reason);
    public static AuthorizationDecision RequireApproval(string reason) => new(AuthorizationOutcome.ApprovalRequired, reason);

    public static AuthorizationDecision RequireApproval(string reason, string toolName,
        IEnumerable<KeyValuePair<string, object?>> arguments) =>
        new(AuthorizationOutcome.ApprovalRequired, reason) { PendingApproval = new(toolName, arguments, reason) };
}

/// <summary>
/// Thrown when a tool invocation is not allowed. Refusals leave the tool-result channel entirely:
/// the host decides what, if anything, the model is told, and an approval request reaches a human
/// instead of becoming text the model can argue with or paraphrase into a false success.
/// </summary>
public sealed class ToolAuthorizationException(AuthorizationDecision decision)
    : InvalidOperationException($"{decision.Outcome}: {decision.Reason}")
{
    public AuthorizationDecision Decision { get; } = decision;
}
