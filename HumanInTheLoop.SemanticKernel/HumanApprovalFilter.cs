using Microsoft.SemanticKernel;

namespace HumanInTheLoop.SemanticKernel;

public class HumanApprovalFilter : IAutoFunctionInvocationFilter
{
    private static readonly HashSet<string> RequiresApproval = ["CreateTicket", "IssueRefund"];

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        var functionName = context.Function.Name;

        if (!RequiresApproval.Contains(functionName))
        {
            await next(context);
            return;
        }

        //Human in the loop checkpoint
        Console.WriteLine($"\n [APPROVAL REQUIRED] The agent wants to call: {functionName}");
        Console.WriteLine($"   Arguments: {context.Arguments}");
        Console.Write("   Do you approve? (y/n): ");

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (input == "y" || input == "yes")
        {
            Console.WriteLine("Approved — executing.");
            await next(context);
        }
        else
        {
            Console.WriteLine("Denied — skipping function.");

            // Override the result so the agent knows approval was denied
            context.Result = new FunctionResult(
                context.Function,
                "APPROVAL_DENIED: The human operator did not approve this action. " +
                "Inform the customer that the action requires manual review and will be processed later.");
            //context.Terminate = true;
        }
    }
}