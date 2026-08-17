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

    // ponytail: in-memory list with O(n) scan — swap for a persistent vector store in production
    private readonly List<(float[] Embedding, ChatResponse Response)> _cache = [];

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

        var best = (Similarity: -1f, Response: (ChatResponse?)null);
        foreach (var (cachedEmbedding, cachedResponse) in _cache)
        {
            var similarity = TensorPrimitives.CosineSimilarity(cachedEmbedding, embedding.Span);
            if (similarity > best.Similarity)
                best = (similarity, cachedResponse);
        }

        LastSimilarity = best.Similarity;
        if (best.Response is not null && best.Similarity >= SimilarityThreshold)
        {
            Hits++;
            return best.Response;
        }

        Misses++;
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        _cache.Add((embedding.ToArray(), response));
        return response;
    }
}
