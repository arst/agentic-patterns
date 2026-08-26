using Microsoft.Extensions.AI;

namespace ToolAuthorization.AgentFramework;

public sealed class AuthorizedAIFunction(
    AIFunction inner,
    RunPrincipal principal,
    ToolCapability capability,
    ToolAuthorizationPolicy policy) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var decision = policy.Authorize(principal, capability, Name, arguments);
        Console.WriteLine($"  [authorization] {Name}: {decision.Outcome} — {decision.Reason}");

        // A refusal is not a tool result. Returning "ApprovalRequired: ..." as text hands the model
        // a sentence to paraphrase — often into a claim that the work was done — and puts the
        // approval request on the channel the model controls. The host gets an exception instead.
        if (decision.Outcome != AuthorizationOutcome.Allowed)
            throw new ToolAuthorizationException(decision);

        var result = await base.InvokeCoreAsync(arguments, cancellationToken);

        // Commit only after the inner call returns. There is deliberately no Release on the
        // exception path: from inside this wrapper the failure is unverified — the effect may
        // already have happened — so the reservation stands and the capability fails closed.
        // Release is a caller-driven act for a failure the caller has verified was pre-effect.
        policy.Commit(capability.Nonce);
        return result;
    }
}
