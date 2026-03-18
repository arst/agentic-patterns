using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Data;

namespace RAG.SemanticKernel;

public sealed class PolicyChunk
{
    [VectorStoreKey] public string Id { get; set; } = "";

    [VectorStoreData(IsIndexed = true)] public string Source { get; set; } = "";

    [VectorStoreData(IsFullTextIndexed = true)]
    [TextSearchResultValue]
    public string Content { get; set; } = "";

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public Embedding<float> Embedding { get; set; }
}