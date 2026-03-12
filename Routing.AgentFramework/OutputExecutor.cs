using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework;

internal sealed class OutputExecutor() : Executor("Output")
{
    [MessageHandler]
    private async ValueTask HandleAsync(string message, IWorkflowContext context)
        => await context.YieldOutputAsync(message);
    
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}