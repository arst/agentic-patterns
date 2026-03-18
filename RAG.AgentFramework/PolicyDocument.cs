using Microsoft.Extensions.VectorData;

public sealed class PolicyDocument
{
    [VectorStoreKey] public required string Id { get; init; }

    [VectorStoreData] public string? SourceName { get; init; }

    [VectorStoreData(IsFullTextSearchable = true)]
    public string? Text { get; init; }

    [VectorStoreVector(1536)] public ReadOnlyMemory<float> TextEmbedding { get; init; }
}