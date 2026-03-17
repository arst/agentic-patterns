using Microsoft.SemanticKernel;

namespace LearningAndAdaptation.SemanticKernel;

public sealed class PolicyInjectionFilter : IPromptRenderFilter
{
    public async Task OnPromptRenderAsync(PromptRenderContext context, Func<PromptRenderContext, Task> next)
    {
        await next(context);

        var sessionId = context.Kernel.Data.TryGetValue("sessionId", out var v) ? v?.ToString() : null;
        if (sessionId is null) return;

        var rules = PolicyStore.GetRules(sessionId);
        if (rules.Count == 0) return;

        var policyBlock = "Behavioral rules you have learned and MUST follow:\n" +
                          string.Join("\n", rules.Select((r, i) => $"{i + 1}. {r}"));

        // Prepend to the already-rendered prompt so it frames everything
        context.RenderedPrompt = policyBlock + "\n\n---\n\n" + context.RenderedPrompt;
    }
}
