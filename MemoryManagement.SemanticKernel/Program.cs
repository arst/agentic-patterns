using System.Net.Http.Headers;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Memory;
using Shared;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0130

var kernel = Settings.Kernel;

var agent = new ChatCompletionAgent
{
    Name = "ReportAgent",
    Instructions = "You are a helpful assistant. Use relevant memories when present.",
    Kernel = kernel
};

using var httpClient = new HttpClient();
httpClient.BaseAddress = new Uri("https://api.mem0.ai");
httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Token", Settings.Mem0ApiSettings.ApiKey);

var mem0Provider = new Mem0Provider(httpClient, options: new Mem0ProviderOptions
{
    UserId = "U1" // use a consistent user ID to have a shared memory across sessions
});

// (Optional) clear for demo
//await mem0Provider.ClearStoredMemoriesAsync();

ChatHistoryAgentThread thread = new();
thread.AIContextProviders.Add(mem0Provider);

// Uncomment to create a memory for the agent
//await agent.InvokeAsync("Remember that I prefer weekly PDF reports, not slides.", thread).FirstAsync();

// 5) Later ask a question that should use memory
var response = await agent.InvokeAsync("Which format for reports do I prefer?", thread).FirstAsync();
Console.WriteLine(response.Message.Content);