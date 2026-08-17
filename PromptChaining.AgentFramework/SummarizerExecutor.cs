using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace PromptChaining.AgentFramework;

internal class SummarizerExecutor(AIAgent summarizerAgent)
    : Executor("Summarizer")
{
    [MessageHandler]
    private async ValueTask HandleAsync(InputWithText message, IWorkflowContext context)
    {
        var people = string.Join(", ", message.Entities.People);
        var orgs = string.Join(", ", message.Entities.Orgs);
        var topics = string.Join(", ", message.Entities.Topics);

        var prompt = $"""
                      Summarize the text in 5 bullet points.
                      Ensure you explicitly mention:
                      - People: {people}
                      - Organizations: {orgs}
                      - Topics: {topics}

                      TEXT:
                      {message.OriginalText}
                      """;

        var response = await summarizerAgent.RunAsync(prompt);
        await context.SendMessageAsync(response.Text);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}