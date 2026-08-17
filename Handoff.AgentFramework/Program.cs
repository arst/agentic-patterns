using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Shared;

var chatClient = Settings.ChatClient;

// Tool for the billing specialist.
[Description("Looks up an invoice and returns its charges.")]
static string GetInvoiceStatus([Description("The invoice number")] string invoiceNumber) =>
    $"Invoice {invoiceNumber}: two identical charges of $49.99 found on the same day. Duplicate charge confirmed, eligible for refund.";

ChatClientAgent triageAgent = new(chatClient,
    """
    You are a triage agent for customer support. Decide which specialist should handle the request:
    - BillingAgent: invoices, refunds, payments
    - TechAgent: bugs, errors, troubleshooting
    - AccountAgent: login, profile, access
    Hand off to the right specialist. If unclear, ask a short clarifying question.
    """,
    "TriageAgent",
    "Routes requests to the right specialist.");

ChatClientAgent billingAgent = new(chatClient,
    "You are a billing specialist. Use your tools to check invoices and solve billing problems concisely.",
    "BillingAgent",
    "Handles billing issues.",
    [AIFunctionFactory.Create(GetInvoiceStatus)]);

ChatClientAgent techAgent = new(chatClient,
    "You are a technical support specialist. Troubleshoot step-by-step.",
    "TechAgent",
    "Handles technical issues.");

ChatClientAgent accountAgent = new(chatClient,
    "You are an account specialist. Help with login/access/profile.",
    "AccountAgent",
    "Handles account issues.");

// Handoff routes: triage can transfer to any specialist, specialists can hand back to triage.
var workflow = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(triageAgent)
    .EmitAgentResponseEvents(true)
    .WithHandoffs(triageAgent, [billingAgent, techAgent, accountAgent])
    .WithHandoff(billingAgent, triageAgent, "Transfer here if it's not billing related")
    .WithHandoff(techAgent, triageAgent, "Transfer here if it's not technical support related")
    .WithHandoff(accountAgent, triageAgent, "Transfer here if it's not account related")
    .Build();

// Scripted customer turns (workflow pauses for user input after each completed turn).
Queue<string> userInputs = new(
[
    "I was charged twice on my last invoice, number INV-1042.",
    "Great, please proceed with the refund. That's all I needed, thank you."
]);

var firstMessage = userInputs.Dequeue();
Console.WriteLine($"User: {firstMessage}");

var run = await InProcessExecution.RunStreamingAsync(workflow, new ChatMessage(ChatRole.User, firstMessage));
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

List<ChatMessage> conversation = [];
var done = false;
await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
{
    switch (evt)
    {
        case AgentResponseEvent { Response: { } response } when !string.IsNullOrWhiteSpace(response.Text):
            var author = response.Messages.LastOrDefault()?.AuthorName ?? response.AgentId;
            Console.WriteLine($"{author}: {response.Text.Trim()}");
            break;

        case WorkflowOutputEvent output:
            conversation = output.As<List<ChatMessage>>() ?? [];
            if (userInputs.TryDequeue(out var next))
            {
                Console.WriteLine($"\nUser: {next}");
                await run.TrySendMessageAsync(new ChatMessage(ChatRole.User, next));
                await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            }
            else
            {
                done = true; // all scripted turns consumed
            }

            break;
    }

    if (done) break;
}

Console.WriteLine("\n=== Final Conversation ===");
foreach (var message in conversation.Where(m => !string.IsNullOrWhiteSpace(m.Text)))
    Console.WriteLine($"{message.AuthorName ?? message.Role.ToString()}: {message.Text.Trim()}");
