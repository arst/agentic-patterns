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
        return decision.Outcome == AuthorizationOutcome.Allowed
            ? await base.InvokeCoreAsync(arguments, cancellationToken)
            : $"{decision.Outcome}: {decision.Reason}";
    }
}
