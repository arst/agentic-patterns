using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework;

internal sealed class ClassifyRouteExecutor() : Executor("ClassifyRoute")
{
    [MessageHandler]
    private ValueTask<RouteDecision> HandleAsync(string ticket, IWorkflowContext context)
    {
        // In a real build: call an LLM agent here and parse structured output.
        // Keep deterministic placeholder for the pattern example.
        RouteDecision decision =
            ticket.Contains("refund", StringComparison.OrdinalIgnoreCase) ? new() { Route = Route.Billing, Reason = "Refund/payment related" } :
            ticket.Contains("error", StringComparison.OrdinalIgnoreCase) ? new() { Route = Route.Technical, Reason = "Bug/error mentioned" } :
            ticket.Contains("login", StringComparison.OrdinalIgnoreCase) ? new() { Route = Route.Account, Reason = "Access/login issue" } :
            new() { Route = Route.General, Reason = "Fallback/default" };

        return ValueTask.FromResult(decision);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}