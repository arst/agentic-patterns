using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework;

internal sealed class TechAgentExecutor() : Executor("TechAgent")
{
    [MessageHandler]
    private ValueTask<string> HandleAsync(RouteDecision decision, IWorkflowContext context)
        => ValueTask.FromResult($"[Tech] Routed because: {decision.Reason}");
    
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}