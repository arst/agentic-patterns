using HumanInTheLoop.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shared;

var builder = new Settings().KernelBuilder;
builder.Plugins.AddFromType<SupportPlugin>();
builder.Services.AddLogging(cfg => cfg.AddConsole().SetMinimumLevel(LogLevel.Warning));
builder.Services.AddSingleton<IAutoFunctionInvocationFilter, HumanApprovalFilter>();

var kernel = builder.Build();
var chat = kernel.GetRequiredService<IChatCompletionService>();

var settings = new PromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

var history = new ChatHistory(
    """
    You are a technical support specialist for an electronics company.
    For technical issues: use TroubleshootIssue first, then CreateTicket if needed.
    For complex issues beyond your capability: use EscalateToHuman.
    For refund requests: use IssueRefund (this requires human approval).
    Be professional and empathetic. Acknowledge frustration while providing clear steps.
    """);

string[] userMessages =
[
    "My smart speaker keeps disconnecting from WiFi every few hours.",
    "I've already tried all of that. I've been dealing with this for 3 weeks and I want a refund.",
    "Actually, I think this might be a hardware defect. Can I speak to a specialist?"
];

foreach (var userMessage in userMessages)
{
    Console.WriteLine($"\nCustomer: {userMessage}");
    history.AddUserMessage(userMessage);

    var response = await chat.GetChatMessageContentAsync(history, settings, kernel);

    Console.WriteLine($"\nAgent: {response.Content}");
    history.AddMessage(response.Role, response.Content ?? "");
}