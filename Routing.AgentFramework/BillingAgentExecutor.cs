using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework;

internal sealed class BillingAgentExecutor() : Executor("BillingAgent")
{
    [MessageHandler]
    private ValueTask<string> HandleAsync(RouteDecision decision, IWorkflowContext context)
        => ValueTask.FromResult($"[Billing] Routed because: {decision.Reason}");

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}