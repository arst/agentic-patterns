using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Routing.AgentFramework;
using Shared;

var setting = new Settings();
var chatClient = new AzureOpenAIClient(
    new Uri(setting.AzureOpenAi.Endpoint),
    new ApiKeyCredential(setting.AzureOpenAi.ApiKey))
    .GetChatClient(setting.AzureOpenAi.ChatModelDeployment)
    .AsIChatClient();

AIAgent router = new ChatClientAgent(
    chatClient,
    name: "router",
    instructions: """
        You are a routing/triage agent.
        Choose the best route for the user's request:

        - Billing: invoices, refunds, charges, payment failures
        - Technical: errors, bugs, performance, integrations
        - Account: login, access, permissions, profile
        - General: everything else

        Return a JSON object matching this schema:
        {
          "route": "Billing|Technical|Account|General",
          "reason": "short explanation",
          "confidence": 0.0
        }

        Rules:
        - Pick ONE route only.
        - Confidence: 0 to 1.
        - If unclear, route=General and set confidence <= 0.55.
    """
);

AIAgent billing = new ChatClientAgent(
    chatClient,
    name: "billing",
    instructions: "You are a billing specialist. Be concise, ask for missing invoice/payment details, propose next steps."
);

AIAgent technical = new ChatClientAgent(
    chatClient,
    name: "technical",
    instructions: "You are a technical support specialist. Troubleshoot step-by-step; ask for logs/errors/version when missing."
);

AIAgent account = new ChatClientAgent(
    chatClient,
    name: "account",
    instructions: "You are an account specialist. Help with login/access/profile and safe remediation steps."
);

AIAgent general = new ChatClientAgent(
    chatClient,
    name: "general",
    instructions: "You are a general assistant. Help the user or ask a clarifying question."
);

var userMessage = "I was charged twice last month and need a refund. Invoice #12345.";

AgentResponse<RouteDecision> routing = await router.RunAsync<RouteDecision>(
    userMessage,
    session: session,
    cancellationToken: CancellationToken.None
);

RouteDecision decision = routing.Result;

Console.WriteLine($"[ROUTE] {decision.Route} (confidence={decision.Confidence:0.00})");
Console.WriteLine($"[REASON] {decision.Reason}");
Console.WriteLine();

// Optional fallback: if low confidence, ask a clarifying question instead of routing hard.
if (decision.Confidence <= 0.55)
{
    var clarification = await general.RunAsync(
        $"Ask one clarifying question to route this request correctly:\n\nUser: {userMessage}",
        session: session
    );

    Console.WriteLine(clarification.Text);
    return;
}

// ---- 5) Dispatch to the specialist agent ----
AIAgent target = decision.Route switch
{
    Route.Billing => billing,
    Route.Technical => technical,
    Route.Account => account,
    _ => general
};

// Non-streaming: RunAsync returns AgentResponse with Text. :contentReference[oaicite:8]{index=8}
AgentResponse response = await target.RunAsync(
    userMessage,
    session: session,
    cancellationToken: CancellationToken.None
);

Console.WriteLine(response.Text);