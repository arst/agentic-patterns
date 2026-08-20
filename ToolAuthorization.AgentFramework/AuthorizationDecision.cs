namespace ToolAuthorization.AgentFramework;

public enum AuthorizationOutcome { Allowed, Denied, ApprovalRequired }

public sealed record AuthorizationDecision(AuthorizationOutcome Outcome, string Reason)
{
    public static AuthorizationDecision Allow() => new(AuthorizationOutcome.Allowed, "Capability permits this invocation.");
    public static AuthorizationDecision Deny(string reason) => new(AuthorizationOutcome.Denied, reason);
    public static AuthorizationDecision RequireApproval(string reason) => new(AuthorizationOutcome.ApprovalRequired, reason);
}
