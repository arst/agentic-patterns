using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework.Workflow.Executors;

internal sealed class ResponseComposer() : Executor("Composer")
{
    [MessageHandler]
    private async ValueTask HandleAsync(
        SpecialistResponse response,
        IWorkflowContext ctx)
    {
        await ctx.YieldOutputAsync(response.Response);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder
            .ConfigureRoutes(r => r.AddHandler<SpecialistResponse>(HandleAsync))
            .YieldsOutputType(typeof(string));
    }
}