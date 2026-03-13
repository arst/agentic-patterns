using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework.Workflow.Executors;

internal sealed class RouterExecutor(
    AIAgent routerAgent) : Executor("Router")
{
    private async ValueTask<RouteDecision> HandleAsync(
        SupportRequest request, IWorkflowContext context)
    {
        var prompt = $$"""
                       Classify the request.

                       {{request.UserMessage}}
                       """;

        var result = await routerAgent.RunAsync<RouteDecision>(prompt);
        var r = await context.ReadStateAsync<SupportRequest>("request");

        return result.Result;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder.ConfigureRoutes(r => r.AddHandler<SupportRequest, RouteDecision>(HandleAsync));
    }
}