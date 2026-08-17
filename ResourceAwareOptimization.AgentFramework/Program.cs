using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Specialized reasoning tier; the fast/default tier comes from shared config
const string ReasoningModelDeployment = "o4-mini";
var fastModelDeployment = Settings.AzureOpenAi.ChatModelDeployment;

var credential = new ApiKeyCredential(Settings.AzureOpenAi.ApiKey);
var azureClient = new AzureOpenAIClient(new Uri(Settings.AzureOpenAi.Endpoint), credential);

var fastModel = (Client: azureClient.GetChatClient(fastModelDeployment).AsIChatClient(),
    ModelId: fastModelDeployment);

var reasoningModel = (Client: azureClient.GetChatClient(ReasoningModelDeployment).AsIChatClient(),
    ModelId: ReasoningModelDeployment);
var budget = new BudgetState(50);

static string ClassifyQuery(IEnumerable<ChatMessage> messages)
{
    var lastUserMsg = messages.LastOrDefault(m => m.Role == ChatRole.User);
    var text = lastUserMsg?.Text ?? "";
    var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    if (wordCount > 50
        || text.Contains("step by step", StringComparison.OrdinalIgnoreCase)
        || text.Contains("analyze", StringComparison.OrdinalIgnoreCase)
        || text.Contains("compare", StringComparison.OrdinalIgnoreCase))
        return "reasoning";

    return "simple";
}

async Task<ChatResponse> RoutingMiddleware(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    IChatClient chatClient,
    CancellationToken cancellationToken)
{
    var tier = ClassifyQuery(messages);
    Console.WriteLine($"  [Router] Classified as: {tier}");

    // Build a fallback chain based on classification
    var chain = tier == "reasoning"
        ? new[] { reasoningModel, fastModel }
        : new[] { fastModel, reasoningModel };

    // If budget is exceeded, force the cheapest model
    if (budget.Exceeded)
    {
        Console.WriteLine("  [Router] Budget exceeded — forcing fast tier.");
        chain = [fastModel];
    }

    foreach (var (client, modelId) in chain)
    {
        try
        {
            Console.WriteLine($"  [Router] Trying: {modelId}");

            // Call the model directly (bypassing 'next' since we're rerouting)
            var response = await client.GetResponseAsync(
                messages.ToList(), options, cancellationToken);

            budget.RecordUsage(modelId, response);

            Console.WriteLine($"  [Router] Success with: {modelId}");
            return response;
        }
        // Only transient failures (HTTP errors, timeouts, network) should trigger the fallback tier
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  [Fallback] {modelId} failed: {ex.Message}");
        }
    }

    // All models failed — call the original pipeline as last resort
    Console.WriteLine("  [Fallback] All tier models failed. Trying original pipeline.");
    return await chatClient.GetResponseAsync(messages, options, cancellationToken);
}

async Task<AgentResponse> BudgetEnforcementMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    if (budget.Exceeded)
    {
        Console.WriteLine("  [BudgetMiddleware] Budget exceeded. Returning early.");
        return new AgentResponse([
            new ChatMessage(ChatRole.Assistant,
                "I've reached my processing budget for this session. " +
                "I can still help with simple questions using a lightweight model, " +
                "but complex analysis will need to wait until the budget resets.")
        ]);
    }

    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
    return response;
}

var client = fastModel.Client
    .AsBuilder()
    .Use(RoutingMiddleware, null)
    .Build();
var agent = new ChatClientAgent(client, """
                                        You are a helpful support agent. Answer user questions concisely and accurately.
                                        For complex reasoning tasks, take your time and think step by step.
                                        For simple factual questions, be brief and direct.
                                        """,
        "ResourceAwareAgent")
    .AsBuilder()
    .Use(BudgetEnforcementMiddleware, null)
    .Build();

var queries = new[]
{
    "What is the capital of France?", // ? simple ? fast
    "Explain step by step why gradient descent converges for convex " +
    "functions and analyze the conditions under which it might diverge " +
    "for non-convex optimization landscapes.", // ? reasoning
    "Hi, how are you?" // ? simple ? fast
};

var session = await agent.CreateSessionAsync();

foreach (var query in queries)
{
    Console.WriteLine($"\nUser: {query}");
    var result = await agent.RunAsync(query, session);
    Console.WriteLine($"Agent: {result}");
}

Console.WriteLine($"\nTotal estimated cost: {budget.TotalCostCents:F2}¢");