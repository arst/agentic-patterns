using System.ComponentModel;

namespace HumanInTheLoop.AgentFramework;

public class SupportPlugin
{
    [Description("Analyze a technical issue and return troubleshooting steps.")]
    public static Task<string> TroubleshootIssue(string issue)
    {
        return Task.FromResult($"""
                                Troubleshooting report for: {issue}
                                1. Verify the device is powered on and connected.
                                2. Restart the device and wait 30 seconds.
                                3. Check for firmware updates.
                                4. If issue persists, a support ticket may be needed.
                                """);
    }

    [Description("Create a support ticket for a customer issue.")]
    public static Task<string> CreateTicket(string issueType, string details)
    {
        return Task.FromResult(
            $$"""{ "status": "created", "ticket_id": "TICKET-{{Random.Shared.Next(1000, 9999)}}", "type": "{{issueType}}" }""");
    }

    [Description("Issue a refund to the customer.")]
    public static Task<string> IssueRefund(string orderId, decimal amount)
    {
        return Task.FromResult(
            $$"""{ "status": "refunded", "order_id": "{{orderId}}", "amount": {{amount}} }""");
    }

    [Description(
        "Escalate the issue to a human specialist. Use when the problem is too complex or emotionally sensitive.")]
    public static Task<string> EscalateToHuman(string issueType, string reason)
    {
        Console.WriteLine("\n[ESCALATION] Transferring to human specialist.");
        Console.WriteLine($"   Issue: {issueType} | Reason: {reason}\n");
        return Task.FromResult(
            """{ "status": "escalated", "message": "A human specialist will take over shortly." }""");
    }
}