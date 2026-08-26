using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SemanticCaching.AgentFramework;

/// <summary>
/// Every dimension that must isolate one cached answer from another. All six are required:
/// a semantic cache keyed on conversation shape alone will happily serve tenant A's answer to
/// tenant B, or an answer generated under a stale tool policy or a stale data revision — a
/// cross-tenant data leak waiting to happen.
/// </summary>
public sealed record CacheNamespace(
    string TenantId,
    string PrincipalScopeHash,
    string SystemPromptHash,
    string ToolSchemaHash,
    string ModelVersion,
    string DataRevision);

/// <summary>Serves cached responses for queries semantically similar to previously answered ones.</summary>
public sealed class SemanticCachingChatClient(
    IChatClient innerClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CacheNamespace ns,
    TimeSpan entryLifetime,
    int maxEntriesPerPartition)
    : DelegatingChatClient(innerClient)
{
    // 0.9 accepts close paraphrases while rejecting merely related questions
    private const float SimilarityThreshold = 0.9f;

    // ponytail: one lock; shard it if this ever leaves a sample
    private readonly object _lock = new();

    // Partitioned by namespace + context: similar user text under a different tenant, system
    // prompt, model, tool policy, data revision, or options must never reuse another partition's
    // answer.
    // ponytail: in-memory dictionary with O(n) scan per partition, and expired entries are only
    // reclaimed when their partition is next read (a partition nobody ever queries again holds
    // its expired entries for the process lifetime, bounded only by maxEntriesPerPartition) —
    // swap for a persistent vector store with its own TTL sweep in production
    private readonly Dictionary<string, List<(float[] Embedding, ChatResponse Response, DateTimeOffset ExpiresAt)>> _cache = [];

    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public float LastSimilarity { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var query = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(query))
            return await base.GetResponseAsync(messageList, options, cancellationToken);

        var embedding = await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        var key = PartitionKey(ns, messageList, options);

        lock (_lock)
        {
            var best = (Similarity: -1f, Response: (ChatResponse?)null);
            if (_cache.TryGetValue(key, out var partition))
            {
                var now = DateTimeOffset.UtcNow;
                partition.RemoveAll(e => e.ExpiresAt <= now);

                foreach (var (cachedEmbedding, cachedResponse, _) in partition)
                {
                    var similarity = TensorPrimitives.CosineSimilarity(cachedEmbedding, embedding.Span);
                    if (similarity > best.Similarity)
                        best = (similarity, cachedResponse);
                }
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
        }

        // The model call happens outside the lock — it can be slow, and must not serialize
        // every other concurrent caller behind it.
        var response = await base.GetResponseAsync(messageList, options, cancellationToken);

        // Clone on store too: the caller's copy of `response` is theirs to mutate freely, so the
        // cache must not keep a reference to the exact object handed back to them.
        var stored = new ChatResponse([.. response.Messages.Select(m => m.Clone())]) { ModelId = response.ModelId };
        var expiresAt = DateTimeOffset.UtcNow + entryLifetime;

        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var partition))
                _cache[key] = partition = [];

            partition.Add((embedding.ToArray(), stored, expiresAt));
            if (partition.Count > maxEntriesPerPartition)
                partition.RemoveAt(0);
        }

        return response;
    }

    // Everything that changes what a valid answer looks like belongs in the key: the namespace
    // (tenant, authorization scope, system prompt, tool policy, model, data revision), every
    // prior turn, and the options. Only the final user message (the embedded query) is excluded
    // — that's what the similarity search matches on.
    public static string PartitionKey(CacheNamespace ns, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages.ToList();
        var lastUser = list.FindLastIndex(m => m.Role == ChatRole.User);
        var priorTurns = string.Join("\n", list.Where((m, i) => i != lastUser).Select(DigestMessage));
        var canonicalOptions = CanonicalOptions(options);

        // Every component is hashed to a fixed-length digest before joining. A raw '|'-join of
        // the raw fields would let a delimiter inside a field (e.g. TenantId "a|b") shift the
        // boundary and collide with an unrelated namespace whose fields split differently — a
        // hash has no delimiter to smuggle across the join, so no combination of field values
        // can produce another combination's key.
        return string.Join('|',
            Hash(ns.TenantId), Hash(ns.PrincipalScopeHash), Hash(ns.SystemPromptHash),
            Hash(ns.ToolSchemaHash), Hash(ns.ModelVersion), Hash(ns.DataRevision),
            Hash(priorTurns), Hash(canonicalOptions));
    }

    // Every option that changes what a valid answer looks like belongs in the key, not just
    // ModelId/Temperature/ResponseFormat — a runtime ChatOptions.Tools that diverges from the
    // namespace's declared ToolSchemaHash, or a different MaxOutputTokens/Seed/StopSequences
    // etc., must not collide with an unrelated request. Deliberately the same shape as
    // EvaluationAndMonitoring.AgentFramework/TraceReplay.cs's TraceStore.CanonicalOptions (the
    // two projects don't reference each other, so this is the pattern copied, not code shared).
    // ConversationId, AllowBackgroundResponses, ContinuationToken and RawRepresentationFactory
    // are excluded: none of them changes what a valid answer looks like.
    private static string CanonicalOptions(ChatOptions? options)
    {
        var tools = string.Join(";", options?.Tools?.Select(tool => tool is AIFunctionDeclaration function
            ? $"{function.Name}:{function.JsonSchema.GetRawText()}"
            : $"{tool.Name}:{tool.Description}") ?? []);
        var toolMode = options?.ToolMode is RequiredChatToolMode required
            ? $"Required:{required.RequiredFunctionName}"
            : options?.ToolMode?.GetType().Name ?? "";
        var additionalProperties = options?.AdditionalProperties is { } props
            ? JsonSerializer.Serialize(props.OrderBy(p => p.Key, StringComparer.Ordinal))
            : "";

        return string.Join('|',
            $"model:{options?.ModelId}", $"temperature:{options?.Temperature}", $"format:{options?.ResponseFormat}",
            $"tools:{tools}", $"toolMode:{toolMode}", $"allowMultipleToolCalls:{options?.AllowMultipleToolCalls}",
            $"instructions:{options?.Instructions}", $"maxOutputTokens:{options?.MaxOutputTokens}",
            $"topP:{options?.TopP}", $"topK:{options?.TopK}", $"seed:{options?.Seed}",
            $"stopSequences:{string.Join(",", options?.StopSequences ?? [])}",
            $"frequencyPenalty:{options?.FrequencyPenalty}", $"presencePenalty:{options?.PresencePenalty}",
            $"reasoningEffort:{options?.Reasoning?.Effort}", $"reasoningOutput:{options?.Reasoning?.Output}",
            $"additionalProperties:{additionalProperties}");
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // Digest every AIContent kind, not just TextContent — a prior function call or its result
    // changes what a valid cached answer looks like just as much as prior text does, and a
    // digest keyed on `.Text` alone silently drops both.
    private static string DigestMessage(ChatMessage m) =>
        $"{m.Role}:{string.Join(",", m.Contents.Select(DigestContent))}";

    private static string DigestContent(AIContent content) => content switch
    {
        TextContent t => $"text:{t.Text}",
        FunctionCallContent c => $"call:{c.CallId}:{c.Name}:{string.Join(",", c.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])}",
        FunctionResultContent r => $"result:{r.CallId}:{r.Result}",
        _ => $"{content.GetType().Name}:{content}"
    };
}
