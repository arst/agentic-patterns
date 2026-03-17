using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace LearningAndAdaptation.AgentFramework;

/// <summary>
/// Step 2 of the learning loop.
/// Receives the (question, answer) pair, asks the LLM to critique the answer
/// and extract zero or more concrete behavioral rules. Persists the rules in
/// PolicyStore so the next turn's AnswerExecutor automatically picks them up.
/// Emits a LearnedRules message which becomes the workflow output.
/// </summary>
public class CritiqueExecutor(ChatClientAgent agent) : Executor("critique")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder.ConfigureRoutes(r => r.AddHandler<AnswerPayload, LearnedRules>(HandleAsync));

    private async ValueTask<LearnedRules> HandleAsync(AnswerPayload payload, IWorkflowContext context)
    {
        const string schema =
            """
            {
              "critique": "<2-3 sentence evaluation summary>",
              "rules": ["<rule 1>", "<rule 2>"]
            }
            """;

        var critiquePrompt =
            $"""
            You just gave this answer to a user:
            ---
            {payload.Answer}
            ---
            Critically evaluate it on three axes:
              • Clarity     – was it easy to follow?
              • Depth       – did it explain the "why", not just the "what"?
              • Conciseness – was there any fluff or repetition?

            Output ONLY valid JSON matching this schema — no markdown fences, no extra text:
            """ + "\n" + schema + """

            Rules must be short, imperative, actionable improvements for FUTURE answers,
            e.g. "Lead with a one-sentence summary before diving into detail."
            If the answer was already excellent, return an empty array for "rules".
            """;

        var response = await agent.RunAsync<CritiqueResult>(critiquePrompt);
        var result = response.Result;

        if (result.Rules.Count > 0)
            PolicyStore.AddRules(payload.SessionId, result.Rules);

        return new LearnedRules(payload.SessionId, result.Rules);
    }

    private record CritiqueResult(string Critique, List<string> Rules);
}
