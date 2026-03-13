using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Parallelization.AgentFramework;

public class ChatExecutor(string name, ChatClientAgent chatClientAgent) : Executor(name)
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder.ConfigureRoutes(routes =>
            routes.AddHandler<ChatMessage, ChatMessage>(HandleAsync));
    }

    private async ValueTask<ChatMessage> HandleAsync(ChatMessage message, IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var response = await chatClientAgent.RunAsync(message, cancellationToken: cancellationToken);
        return new ChatMessage(ChatRole.Assistant, response.Text);
    }
}