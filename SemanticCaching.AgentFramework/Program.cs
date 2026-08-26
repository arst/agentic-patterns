using System.ClientModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SemanticCaching.AgentFramework;
using Shared;

var azureClient = new AzureOpenAIClient(new Uri(Settings.AzureOpenAi.Endpoint),
    new ApiKeyCredential(Settings.AzureOpenAi.ApiKey));

var embeddingGenerator = azureClient
    .GetEmbeddingClient(Settings.AzureOpenAi.EmbeddingModelDeployment)
    .AsIEmbeddingGenerator();

const string systemPrompt = "You are a concise assistant. Answer in one or two sentences.";

// Every dimension a real deployment must not forget: which tenant, under which authorization
// scope, which system prompt, which tool policy, which model, and which revision of the
// underlying data the answer was drawn from. This sample has one caller, no tools and one
// static document set, so most of these are constants — a real deployment reads TenantId and
// PrincipalScopeHash from the caller's auth context per request.
var cacheNamespace = new CacheNamespace(
    TenantId: "sample-tenant",
    PrincipalScopeHash: "sample-principal",
    SystemPromptHash: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(systemPrompt))),
    ToolSchemaHash: "no-tools", // CachingAgent exposes no tools; hash the registered schema once it does
    ModelVersion: Settings.AzureOpenAi.ChatModelDeployment,
    DataRevision: "v1"); // bump whenever the knowledge this agent answers from changes

// Cheapest check first: exact-match cache (free hash lookup) is outermost, then the
// semantic cache (costs one embedding call), then the real model.
SemanticCachingChatClient semanticCache = null!;
var client = new ChatClientBuilder(Settings.ChatClient)
    .UseDistributedCache(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())))
    .Use(inner => semanticCache = new SemanticCachingChatClient(
        inner, embeddingGenerator, cacheNamespace,
        entryLifetime: TimeSpan.FromMinutes(10), maxEntriesPerPartition: 500))
    .Build();

var agent = new ChatClientAgent(client, systemPrompt, "CachingAgent");

(string Label, string Query)[] calls =
[
    ("new question", "What is the capital of France?"),
    ("identical question", "What is the capital of France?"),
    ("paraphrase", "Which city is the capital of France?"),
    ("unrelated question", "How does photosynthesis work?")
];

foreach (var (label, query) in calls)
{
    var invocationsBefore = semanticCache.Hits + semanticCache.Misses;
    var hitsBefore = semanticCache.Hits;

    Console.WriteLine($"User ({label}): {query}");
    var stopwatch = Stopwatch.StartNew();
    var result = await agent.RunAsync(query); // no shared session — each query is a standalone request
    stopwatch.Stop();

    // Exact hits are answered before the request ever reaches the semantic layer
    var status = semanticCache.Hits + semanticCache.Misses == invocationsBefore
        ? "HIT(exact)"
        : semanticCache.Hits > hitsBefore
            ? $"HIT(semantic ~{semanticCache.LastSimilarity:F2})"
            : "MISS";

    Console.WriteLine($"  [{status}] {stopwatch.ElapsedMilliseconds} ms");
    Console.WriteLine($"Agent: {result}\n");
}

var llmCalls = semanticCache.Misses;
Console.WriteLine($"Summary: {calls.Length} queries, {llmCalls} LLM calls, {calls.Length - llmCalls} saved by caching.");
