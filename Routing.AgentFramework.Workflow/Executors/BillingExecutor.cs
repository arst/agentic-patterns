using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework.Workflow.Executors;

internal sealed class BillingExecutor : Executor
{
    private readonly AIAgent _agent;

    public BillingExecutor(AIAgent agent) : base("billing")
    {
        _agent = agent;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol)
    {
        return protocol.ConfigureRoutes(r => r.AddHandler<RouteDecision, SpecialistResponse>(HandleAsync));
    }

    private async ValueTask<SpecialistResponse> HandleAsync(
        RouteDecision decision,
        IWorkflowContext ctx)
    {
        var request = await ctx.ReadStateAsync<SupportRequest>("request", "global");

        var prompt =
            $"""
             You are a billing specialist.

             Request:
             {request!.UserMessage}
             """;

        var response = await _agent.RunAsync(prompt);

        return new SpecialistResponse(response.Text);
    }
}