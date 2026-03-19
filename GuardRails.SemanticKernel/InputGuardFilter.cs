using Microsoft.SemanticKernel;

namespace GuardRails.SemanticKernel;

internal class InputGuardFilter : IPromptRenderFilter
{
    public async Task OnPromptRenderAsync(
        PromptRenderContext context, Func<PromptRenderContext, Task> next)
    {
        // Let the prompt render first so we can inspect it
        await next(context);

        var prompt = context.RenderedPrompt ?? "";

        // Check 1: Prompt injection detection
        if (SafetyChecks.IsInjection(prompt))
        {
            Console.WriteLine("  [InputGuard] BLOCKED: Prompt injection detected.");
            // Setting context.Result prevents the LLM call entirely
            context.Result = new FunctionResult(context.Function,
                "I'm sorry, I can't process that request. " +
                "If you need help, please rephrase your question.");
            return;
        }

        // Check 2: Blocked topic detection
        if (SafetyChecks.IsBlockedTopic(prompt))
        {
            Console.WriteLine("  [InputGuard] BLOCKED: Sensitive topic detected.");
            context.Result = new FunctionResult(context.Function,
                "I'm not able to help with requests involving credentials or sensitive keys. " +
                "Please contact your IT administrator.");
            return;
        }

        // Check 3: PII redaction — modify the prompt before the LLM sees it
        if (SafetyChecks.HasPii(prompt))
        {
            Console.WriteLine("  [InputGuard] PII detected — redacting from prompt.");
            context.RenderedPrompt = SafetyChecks.RedactPii(prompt);
        }
    }
}