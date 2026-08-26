using CodeAct.AgentFramework.Execution;
using Microsoft.Extensions.AI;

namespace AgenticPatterns.Tests;

/// <summary>Records the token it was invoked with, instead of actually running anything.</summary>
internal sealed class RecordingCodeRunner : IGeneratedCodeRunner
{
    public CancellationToken ReceivedToken { get; private set; }

    public Task<ExecutionResult> RunAsync(string sourceCode, CancellationToken cancellationToken)
    {
        ReceivedToken = cancellationToken;
        return Task.FromResult(new ExecutionResult(0, "", "", TimedOut: false));
    }
}

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
                // Default is orthogonal to every registered vector — an unregistered
                // input must never accidentally look similar to a registered one.
                vectors.TryGetValue(v, out var vec) ? vec : [0f, 0f, 1f]))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Shared by every test that flips a process environment variable for a double-opt-in
/// gate (CodeAct, StigmergicCoordination, EvaluationAndMonitoring). xunit runs tests in one
/// class sequentially, so mutating the process environment here cannot race another test in
/// the same class.</summary>
internal static class TestEnvironment
{
    public static T WithEnvironmentVariable<T>(string name, string? value, Func<T> body)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }
}
