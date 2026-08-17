using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace PromptChaining.AgentFramework;

internal class ExtractorExecutor(IChatClient chatClient)
    : Executor("Extractor")
{
    private readonly ChatClientAgent _agent = new(chatClient, name: "ExtractorAgent",
        instructions: """
                      You are an information extraction engine.
                      Extract people, organizations, and topics from the text.
                      """);

    [MessageHandler]
    private async ValueTask HandleAsync(string input, IWorkflowContext context)
    {
        var response = await _agent.RunAsync<ExtractedEntities>(input);
        await context.SendMessageAsync(new InputWithText(response.Result, input));
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}
