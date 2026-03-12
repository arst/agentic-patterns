using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework;

internal sealed class GeneralAgentExecutor() : Executor("GeneralAgent")
{
    [MessageHandler]
    private ValueTask<string> HandleAsync(RouteDecision decision, IWorkflowContext context)
        => ValueTask.FromResult($"[General] Routed because: {decision.Reason}");
    
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}