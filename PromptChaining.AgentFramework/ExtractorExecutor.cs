using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace PromptChaining.AgentFramework;

internal class ExtractorExecutor(IChatClient chatClient)
    : Executor("Extractor")
{
    [MessageHandler]
    private async ValueTask HandleAsync(string input, IWorkflowContext context)
    {
        var agent = new ChatClientAgent(chatClient, name: "ExtractorAgent",
            instructions: """
                          You are an information extraction engine.
                          Extract people, organizations, and topics from the text.
                          Output ONLY valid JSON: { "people": [...], "orgs": [...], "topics": [...] }
                          """);

        var response = await agent.RunAsync(input);
        await context.SendMessageAsync(new InputWithText(response.Text, input));
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}