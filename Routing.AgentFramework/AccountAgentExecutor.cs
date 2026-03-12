using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework;

internal sealed class AccountAgentExecutor() : Executor("AccountAgent")
{
    [MessageHandler]
    private ValueTask<string> HandleAsync(RouteDecision decision, IWorkflowContext context)
        => ValueTask.FromResult($"[Account] Routed because: {decision.Reason}");
    
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}