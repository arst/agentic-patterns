using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace LearningAndAdaptation.AgentFramework;

/// <summary>
///     Step 1 of the learning loop.
///     Receives a question, prepends any already-learned policy rules, then answers.
///     Passes (question, answer) to the CritiqueExecutor.
/// </summary>
public class AnswerExecutor(ChatClientAgent agent) : Executor("answer")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        return protocolBuilder.ConfigureRoutes(r => r.AddHandler<TurnInput, AnswerPayload>(HandleAsync));
    }

    private async ValueTask<AnswerPayload> HandleAsync(TurnInput input, IWorkflowContext context)
    {
        var rules = PolicyStore.GetRules(input.SessionId);

        var policyBlock = rules.Count > 0
            ? "Behavioral rules you have learned and MUST follow:\n" +
              string.Join("\n", rules.Select((r, i) => $"{i + 1}. {r}")) +
              "\n\n---\n\n"
            : string.Empty;

        var prompt = policyBlock + input.Question;
        var response = await agent.RunAsync(prompt);

        return new AnswerPayload(input.SessionId, input.Question, response.Text);
    }
}