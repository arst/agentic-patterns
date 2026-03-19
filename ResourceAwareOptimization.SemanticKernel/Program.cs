using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ResourceAwareOptimization.SemanticKernel;
using Shared;

var setting = new Settings();
var builder = setting.KernelBuilder;

// Fast tier — cheap, low-latency, good for factual recall and simple tasks
builder.AddAzureOpenAIChatCompletion(
    "gpt-4o-mini",
    setting.AzureOpenAi.Endpoint,
    setting.AzureOpenAi.ApiKey,
    "fast");

// Reasoning tier — expensive, high-capability, for complex multi-step reasoning
builder.AddAzureOpenAIChatCompletion(
    "o4-mini",
    setting.AzureOpenAi.Endpoint,
    setting.AzureOpenAi.ApiKey,
    "reasoning");

// Default tier — mid-range, used for the router/classifier itself
builder.AddAzureOpenAIChatCompletion(
    "gpt-4o",
    setting.AzureOpenAi.Endpoint,
    setting.AzureOpenAi.ApiKey,
    "default");

var kernel = builder.Build();

var budgetTracker = new BudgetTracker(50, 0);

kernel.FunctionInvocationFilters.Add(budgetTracker);

string ClassifyQuery(string query)
{
    // Heuristic tier — zero LLM cost
    var wordCount = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    if (wordCount < 10 && !query.Contains("why", StringComparison.OrdinalIgnoreCase)
                       && !query.Contains("explain", StringComparison.OrdinalIgnoreCase)
                       && !query.Contains("compare", StringComparison.OrdinalIgnoreCase))
        return "simple";

    if (query.Contains("step by step", StringComparison.OrdinalIgnoreCase)
        || query.Contains("analyze", StringComparison.OrdinalIgnoreCase)
        || query.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
        || wordCount > 50)
        return "reasoning";

    return "simple";
}

async Task<string> HandleQueryAsync(string userQuery)
{
    var tier = ClassifyQuery(userQuery);
    Console.WriteLine($"[Router] Classified as: {tier}");

    var serviceId = tier switch
    {
        "reasoning" => "reasoning",
        _ => "fast"
    };

    string[] fallbackChain = serviceId == "reasoning"
        ? ["reasoning", "default", "fast"]
        : ["fast", "default"];

    foreach (var sid in fallbackChain)
        try
        {
            Console.WriteLine($"  [Router] Trying model: {sid}");

            var chatService = kernel.Services
                .GetRequiredKeyedService<IChatCompletionService>(sid);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a helpful support agent. Answer the user's question concisely.");
            history.AddUserMessage(userQuery);

            var settings = new OpenAIPromptExecutionSettings
            {
                ServiceId = sid,
                Temperature = tier == "reasoning" ? 0.2 : 0.7
            };

            var response = await chatService.GetChatMessageContentAsync(
                history, settings, kernel);

            Console.WriteLine($"  [Router] Success with: {sid}");
            return response.Content ?? "No response generated.";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Fallback] {sid} failed: {ex.Message}");
        }

    return "All models unavailable. Please try again later.";
}

var queries = new[]
{
    "What is the capital of France?", // → simple → fast
    "Explain step by step why gradient descent converges for convex " +
    "functions and analyze the conditions under which it might diverge " +
    "for non-convex optimization landscapes.", // → reasoning
    "Hi" // → simple → fast
};

foreach (var query in queries)
{
    Console.WriteLine($"\n👤 User: {query}");
    var answer = await HandleQueryAsync(query);
    Console.WriteLine($"Agent: {answer}");

    if (budgetTracker.BudgetExceeded)
    {
        Console.WriteLine("\n⚠Budget limit reached. Switching all remaining queries to fast tier.");
        break;
    }
}

Console.WriteLine($"\nTotal estimated cost: {budgetTracker.TotalCostCents:F2}¢");