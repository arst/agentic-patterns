using Microsoft.SemanticKernel;

internal class OutputGuardFilter : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        await next(context);

        var response = context.Result.ToString() ?? "";

        // FunctionResult(FunctionResult, object?) copies Function, Metadata and Culture from the
        // original result and only swaps the value, so rewriting the text no longer drops that
        // metadata the way `new FunctionResult(context.Function, text)` did.
        if (SafetyChecks.HasPii(response))
        {
            Console.WriteLine("  🛡️ [OutputGuard] PII detected in response — redacting.");
            context.Result = new FunctionResult(context.Result, SafetyChecks.RedactPii(response));
            return;
        }

        if (response.Length > 2000)
        {
            Console.WriteLine("  🛡️ [OutputGuard] Response too long — truncating.");
            context.Result = new FunctionResult(context.Result,
                response[..2000] + "\n\n[Response truncated for safety.]");
        }
    }
}