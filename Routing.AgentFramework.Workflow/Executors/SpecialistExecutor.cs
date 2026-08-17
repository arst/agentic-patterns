using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework.Workflow.Executors;

internal sealed class SpecialistExecutor : Executor
{
    private readonly AIAgent _agent;
    private readonly string _role;

    public SpecialistExecutor(AIAgent agent, string role) : base(role)
    {
        _agent = agent;
        _role = role;
    }

    [MessageHandler]
    private async ValueTask<SpecialistResponse> HandleAsync(
        RouteDecision decision,
        IWorkflowContext ctx)
    {
        var req = await ctx.ReadStateAsync<SupportRequest>("request", "global");

        var prompt =
            $"""
             You are the {_role} specialist.

             Request:
             {req!.UserMessage}

             Provide the best support answer.
             """;

        var result = await _agent.RunAsync(prompt);

        return new SpecialistResponse(result.Text);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder.ConfigureRoutes(r => r.AddHandler<RouteDecision, SpecialistResponse>(HandleAsync));
    }
}
