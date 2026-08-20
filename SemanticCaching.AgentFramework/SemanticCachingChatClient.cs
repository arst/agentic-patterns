using System.Numerics.Tensors;
using Microsoft.Extensions.AI;

namespace SemanticCaching.AgentFramework;

/// <summary>Serves cached responses for queries semantically similar to previously answered ones.</summary>
public sealed class SemanticCachingChatClient(
    IChatClient innerClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    : DelegatingChatClient(innerClient)
{
    // 0.9 accepts close paraphrases while rejecting merely related questions
    private const float SimilarityThreshold = 0.9f;

    // Partitioned by context: similar user text under a different system prompt, model,
    // or options must never reuse another context's answer.
    // ponytail: in-memory dictionary with O(n) scan per partition — swap for a persistent vector store in production
    private readonly Dictionary<string, List<(float[] Embedding, ChatResponse Response)>> _cache = [];

    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public float LastSimilarity { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var query = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(query))
            return await base.GetResponseAsync(messages, options, cancellationToken);

        var embedding = await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken);

        var key = ContextKey(messages, options);
        if (!_cache.TryGetValue(key, out var partition))
            _cache[key] = partition = [];

        var best = (Similarity: -1f, Response: (ChatResponse?)null);
        foreach (var (cachedEmbedding, cachedResponse) in partition)
        {
            var similarity = TensorPrimitives.CosineSimilarity(cachedEmbedding, embedding.Span);
            if (similarity > best.Similarity)
                best = (similarity, cachedResponse);
        }

        LastSimilarity = best.Similarity;
        if (best.Response is not null && best.Similarity >= SimilarityThreshold)
        {
            Hits++;
            // Hand out a copy, not the shared cached instance (its Usage/ResponseId belong
            // to the original call — a cache hit costs no tokens).
            return new ChatResponse([.. best.Response.Messages.Select(m => m.Clone())])
            {
                ModelId = best.Response.ModelId
            };
        }

        Misses++;
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        partition.Add((embedding.ToArray(), response));
        return response;
    }

    // Everything that changes what a valid answer looks like belongs in the key.
    private static string ContextKey(IEnumerable<ChatMessage> messages, ChatOptions? options) =>
        string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)) +
        $"|{options?.ModelId}|{options?.Temperature}|{options?.ResponseFormat}";
}
