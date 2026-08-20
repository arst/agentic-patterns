using Microsoft.Extensions.AI;

namespace AgenticPatterns.Tests;

/// <summary>Returns pre-canned responses in order and counts calls.</summary>
internal sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
{
    private int _next;

    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(responses[Math.Min(_next++, responses.Length - 1)]);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Maps input strings to fixed vectors; unknown inputs get a default vector.</summary>
internal sealed class FixedEmbeddingGenerator(Dictionary<string, float[]> vectors)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(v => new Embedding<float>(
                vectors.TryGetValue(v, out var vec) ? vec : [1f, 0f, 0f]))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
