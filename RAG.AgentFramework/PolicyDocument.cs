using Microsoft.Extensions.VectorData;

namespace RAG.AgentFramework;

public sealed class PolicyDocument
{
    [VectorStoreKey] public required string Id { get; init; }

    [VectorStoreData] public string? SourceName { get; init; }

    [VectorStoreData(IsFullTextIndexed = true)]
    public string? Text { get; init; }

    // string-typed vector property: the store's EmbeddingGenerator embeds it on upsert/search
    [VectorStoreVector(1536)] public string? TextEmbedding { get; init; }
}