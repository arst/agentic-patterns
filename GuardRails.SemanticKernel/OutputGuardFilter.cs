using Microsoft.SemanticKernel;

internal class OutputGuardFilter : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        await next(context);

        var response = context.Result.ToString() ?? "";

        if (SafetyChecks.HasPii(response))
        {
            Console.WriteLine("  🛡️ [OutputGuard] PII detected in response — redacting.");
            context.Result = new FunctionResult(context.Function,
                SafetyChecks.RedactPii(response));
            return;
        }

        if (response.Length > 2000)
        {
            Console.WriteLine("  🛡️ [OutputGuard] Response too long — truncating.");
            context.Result = new FunctionResult(context.Function,
                response[..2000] + "\n\n[Response truncated for safety.]");
        }
    }
}