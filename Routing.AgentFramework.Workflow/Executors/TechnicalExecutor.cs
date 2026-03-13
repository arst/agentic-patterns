using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework.Workflow.Executors;

internal sealed class TechnicalExecutor(AIAgent agent) : Executor("technical")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol)
    {
        return protocol.ConfigureRoutes(r => r.AddHandler<RouteDecision, SpecialistResponse>(HandleAsync));
    }

    private async ValueTask<SpecialistResponse> HandleAsync(
        RouteDecision decision,
        IWorkflowContext ctx)
    {
        var request = await ctx.ReadStateAsync<SupportRequest>("request");

        var prompt =
            $"""
             You are a technical support specialist. Troubleshoot step-by-step; ask for logs/errors/version when missing.

             Request:
             {request!.UserMessage}
             """;

        var response = await agent.RunAsync(prompt);

        return new SpecialistResponse(response.Text);
    }
}