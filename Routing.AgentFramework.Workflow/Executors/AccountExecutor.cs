using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework.Workflow.Executors;

internal sealed class AccountExecutor(AIAgent agent) : Executor("account")
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
             You are an account specialist. Help with login/access/profile and safe remediation steps.

             Request:
             {request!.UserMessage}
             """;

        var response = await agent.RunAsync(prompt);

        return new SpecialistResponse(response.Text);
    }
}