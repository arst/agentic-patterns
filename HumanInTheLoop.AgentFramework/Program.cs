#pragma warning disable MEAI001
using HumanInTheLoop.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Normal tools - can be called without approval
var troubleshootTool = AIFunctionFactory.Create(SupportPlugin.TroubleshootIssue);
var escalateTool = AIFunctionFactory.Create(SupportPlugin.EscalateToHuman);
// Sensitive tools - need approval - wrapped with ApprovalRequiredAIFunction
AIFunction createTicketTool = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create(SupportPlugin.CreateTicket));
AIFunction issueRefundTool = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create(SupportPlugin.IssueRefund));
var chatClient = new Settings().ChatClient;

var agent = new ChatClientAgent(
    chatClient,
    """
    You are a technical support specialist for an electronics company.
    For technical issues: use TroubleshootIssue first, then CreateTicket if needed.
    For complex issues beyond your capability: use EscalateToHuman.
    For refund requests: use IssueRefund.
    Be professional and empathetic.
    """,
    "SupportAgent",
    tools: [troubleshootTool, createTicketTool, issueRefundTool, escalateTool]);

var session = await agent.CreateSessionAsync();

string[] customerMessages =
[
    "My smart speaker keeps disconnecting from WiFi every few hours.",
    "I've tried all that already. It's been 3 weeks. Can you create a ticket?",
    "Actually forget the ticket, I just want a refund for order ORD-5521."
];

foreach (var message in customerMessages)
{
    Console.WriteLine($"\n Customer: {message}");
    var response = await agent.RunAsync(message, session);

    // Check if the agent is requesting approval for any tool calls
    var approvalRequests = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<FunctionApprovalRequestContent>()
        .ToList();

    // Handle approval requests in a loop (there may be multiple)
    while (approvalRequests.Count > 0)
    {
        foreach (var request in approvalRequests)
        {
            Console.WriteLine($"\n[APPROVAL REQUIRED] Agent wants to call: {request.FunctionCall.Name}");
            Console.WriteLine($"   Arguments: {string.Join(", ",
                request.FunctionCall.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])}");
            Console.Write("   Approve? (y/n): ");

            var input = Console.ReadLine()?.Trim().ToLowerInvariant();
            var approved = input is "y" or "yes";

            Console.WriteLine(approved ? "   V Approved." : "   X Denied.");

            // Send the human's decision back to the agent using CreateResponse
            var approvalMessage = new ChatMessage(ChatRole.User,
                [request.CreateResponse(approved)]);

            response = await agent.RunAsync([approvalMessage], session);
        }

        // Check if the new response has more approval requests
        approvalRequests = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionApprovalRequestContent>()
            .ToList();
    }

    // Print the agent's final response (after all approvals are resolved)
    Console.WriteLine($"\nAgent: {response}");
}