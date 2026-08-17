using System.ClientModel;
using System.Diagnostics;
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

// Cheapest check first: exact-match cache (free hash lookup) is outermost, then the
// semantic cache (costs one embedding call), then the real model.
SemanticCachingChatClient semanticCache = null!;
var client = new ChatClientBuilder(Settings.ChatClient)
    .UseDistributedCache(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())))
    .Use(inner => semanticCache = new SemanticCachingChatClient(inner, embeddingGenerator))
    .Build();

var agent = new ChatClientAgent(client,
    "You are a concise assistant. Answer in one or two sentences.",
    "CachingAgent");

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
