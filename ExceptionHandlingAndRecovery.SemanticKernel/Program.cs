using ExceptionHandlingAndRecovery.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Shared;

var builder = Settings.CreateKernelBuilder();
builder.Plugins.AddFromType<LocationPlugin>();
builder.Services.AddLogging(cfg => cfg.AddConsole().SetMinimumLevel(LogLevel.Information));
builder.Services.AddSingleton<IFunctionInvocationFilter, RetryAndFallbackFilter>();
var kernel = builder.Build();

var settings = new PromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

try
{
    var result = await kernel.InvokePromptAsync(
        "Find the precise location of '15 Rue de Rivoli, Paris, France'.",
        new KernelArguments(settings));

    Console.WriteLine($"\nFinal result: {result}");
}
catch (Exception ex)
{
    // Terminal fallback — if even the fallback fails, escalate
    Console.WriteLine($"\n[Escalation] All recovery strategies exhausted: {ex.Message}");
    Console.WriteLine("   ? Routing to human operator for manual resolution.");
}