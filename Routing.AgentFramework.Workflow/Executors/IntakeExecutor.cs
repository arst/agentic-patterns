using Microsoft.Agents.AI.Workflows;

namespace Routing.AgentFramework.Workflow.Executors;

internal sealed class IntakeExecutor() : Executor<string>("Intake")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol)
    {
        return protocol
            .ConfigureRoutes(r => r.AddHandler<string>(HandleAsync))
            .SendsMessageType(typeof(SupportRequest));
    }

    public override async ValueTask HandleAsync(string message, IWorkflowContext context,
        CancellationToken cancellationToken = new())
    {
        await context.QueueStateUpdateAsync("request", new SupportRequest(message), "global", cancellationToken);
        await context.SendMessageAsync(new SupportRequest(message), cancellationToken);
    }
}