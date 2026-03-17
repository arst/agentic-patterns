using Microsoft.SemanticKernel;

namespace LearningAndAdaptation.SemanticKernel;

/// <summary>
/// Logs every tool call the agent makes — makes the self-learning loop visible.
/// </summary>
public sealed class ToolCallLoggingFilter : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var args = string.Join(", ", context.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
        Console.WriteLine($"\n[agent tool call] {context.Function.PluginName}.{context.Function.Name}({args})");
        await next(context);
        Console.WriteLine($"[tool result] {context.Result.GetValue<string>()}");
    }
}