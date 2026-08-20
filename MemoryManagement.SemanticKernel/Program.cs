using System.Net.Http.Headers;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Memory;
using Shared;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0130

var kernel = Settings.Kernel;

var tenantId = Environment.GetEnvironmentVariable("MEM0_TENANT_ID") ?? "demo-tenant";
var userId = Environment.GetEnvironmentVariable("MEM0_USER_ID") ?? "U1";
if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId))
    throw new InvalidOperationException("MEM0_TENANT_ID and MEM0_USER_ID cannot be empty.");
var scopedUserId = $"{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(userId)}";

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
    // Mem0 exposes one user key, so namespace it by the authenticated tenant and subject.
    UserId = scopedUserId
});

Console.WriteLine("Mem0 tenant/user scope configured.");

// (Optional) clear only this tenant/user scope for the demo.
//await mem0Provider.ClearStoredMemoriesAsync();

ChatHistoryAgentThread thread = new();
thread.AIContextProviders.Add(mem0Provider);

// Uncomment to create a memory for the agent
//await agent.InvokeAsync("Remember that I prefer weekly PDF reports, not slides.", thread).FirstAsync();

// 5) Later ask a question that should use memory
var response = await agent.InvokeAsync("Which format for reports do I prefer?", thread).FirstAsync();
Console.WriteLine(response.Message.Content);
