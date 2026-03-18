using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace HumanInTheLoop.SemanticKernel;

public class SupportPlugin
{
    [KernelFunction]
    [Description("Analyze a technical issue and return troubleshooting steps.")]
    public Task<string> TroubleshootIssue(string issue)
    {
        return Task.FromResult(
            $"""
             Troubleshooting report for: {issue}
             1. Verify the device is powered on and connected.
             2. Restart the device and wait 30 seconds.
             3. Check for firmware updates.
             4. If issue persists, a support ticket may be needed.
             """);
    }

    [KernelFunction]
    [Description("Create a support ticket. Requires human approval before execution.")]
    public Task<string> CreateTicket(string issueType, string details)
    {
        // In a real system, this writes to a ticketing system
        var ticketId = $"TICKET-{Random.Shared.Next(1000, 9999)}";
        return Task.FromResult(
            $"{{ \"status\": \"created\", \"ticket_id\": \"{ticketId}\", \"type\": \"{issueType}\", \"details\": \"{details}\" }}");
    }

    [KernelFunction]
    [Description("Issue a refund to the customer. Requires human approval before execution.")]
    public Task<string> IssueRefund(string orderId, decimal amount)
    {
        return Task.FromResult(
            $"{{ \"status\": \"refunded\", \"order_id\": \"{orderId}\", \"amount\": {amount} }}");
    }

    [KernelFunction]
    [Description(
        "Escalate the issue to a human specialist. Use this when the problem is too complex, " +
        "emotionally sensitive, or beyond your troubleshooting capabilities.")]
    public Task<string> EscalateToHuman(string issueType, string reason)
    {
        // In production: push to a human review queue, notify via Slack/Teams, etc.
        Console.WriteLine("\n📢 [ESCALATION] Agent is escalating to a human specialist.");
        Console.WriteLine($"   Issue type : {issueType}");
        Console.WriteLine($"   Reason     : {reason}");
        Console.WriteLine("   → Transferred to human queue.\n");

        return Task.FromResult(
            $"{{ \"status\": \"escalated\", \"issue_type\": \"{issueType}\", \"message\": \"A human specialist will take over shortly.\" }}");
    }
}