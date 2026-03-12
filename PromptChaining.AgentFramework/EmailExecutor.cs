using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace PromptChaining.AgentFramework;

internal class EmailExecutor(AIAgent emailAgent)
    : Executor("EmailGenerator")
{
    [MessageHandler]
    private async ValueTask HandleAsync(string summary, IWorkflowContext context)
    {
        var prompt = $"""
                      Write a concise internal email (<= 150 words) to leadership.

                      SUMMARY:
                      {summary}
                      """;

        var response = await emailAgent.RunAsync(prompt);
        await context.SendMessageAsync(response.Text);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder;
    }
}