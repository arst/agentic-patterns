using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace PromptChaining.AgentFramework;

internal class SummarizerExecutor(AIAgent summarizerAgent) 
    : Executor("Summarizer")
{
    [MessageHandler]
    private async ValueTask HandleAsync(InputWithText message, IWorkflowContext context)
    {
        using var doc = JsonDocument.Parse(message.RawJson);
        var root   = doc.RootElement;
        var people = string.Join(", ", root.GetProperty("people").EnumerateArray().Select(e => e.GetString()));
        var orgs   = string.Join(", ", root.GetProperty("orgs").EnumerateArray().Select(e => e.GetString()));
        var topics = string.Join(", ", root.GetProperty("topics").EnumerateArray().Select(e => e.GetString()));

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