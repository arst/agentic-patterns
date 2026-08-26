using System.Net;
using System.Text;
using System.Text.Json;
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
/// gate (CodeAct, StigmergicCoordination, EvaluationAndMonitoring) — now three classes, not one.
/// xunit runs tests within a class sequentially, but parallelises across classes by default,
/// which is unsafe for two of the three: <c>CodeActExecutionTests</c> and
/// <c>StigmergicBuildGateTests</c> both mutate
/// <c>AGENTIC_PATTERNS_ACKNOWLEDGE_UNSAFE_CODE_EXECUTION</c>, so both carry
/// <c>[Collection("process-environment")]</c> to force them onto the same xunit collection and
/// stop them interleaving. <c>ProductionControlsPhaseTwoTests</c> uses a different variable
/// (<c>AGENTIC_PATTERNS_ACKNOWLEDGE_FULL_TRACE_CAPTURE</c>), so it has nothing to collide
/// with and needs no collection.</summary>
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

/// <summary>Stands in for the OpenAI endpoint at the <see cref="HttpMessageHandler"/> seam: no
/// port, no socket, no real network call. Answers every chat-completion request with an assistant
/// message that calls back whichever function the request offered, repeated
/// <paramref name="toolCallsPerTurn"/> times per response — so a Semantic Kernel auto-invocation
/// loop driven against this handler only ever stops if something (a filter) stops it.</summary>
internal sealed class ScriptedToolCallHttpHandler(int toolCallsPerTurn = 1) : HttpMessageHandler
{
    private int _requestCount;

    public int RequestCount => _requestCount;

    /// <summary>Every request body this handler has answered, in order — lets a test assert the
    /// model was never fed a budget-refusal message to paraphrase.</summary>
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (RequestBodies) RequestBodies.Add(body);
        var requestNumber = Interlocked.Increment(ref _requestCount);

        using var doc = JsonDocument.Parse(body);
        var toolName = doc.RootElement.GetProperty("tools")[0].GetProperty("function")
            .GetProperty("name").GetString();

        // Built via object graph + JsonSerializer, not a hand-assembled string: OpenAI's
        // chat-completion response has enough nested braces that a raw string literal fights
        // its own interpolation syntax.
        var toolCalls = Enumerable.Range(0, toolCallsPerTurn).Select(i => new
        {
            id = $"call_{requestNumber}_{i}",
            type = "function",
            function = new { name = toolName, arguments = "{}" }
        });
        var responseBody = new
        {
            id = $"chatcmpl-{requestNumber}",
            @object = "chat.completion",
            created = 0,
            model = "stub-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = (string?)null, tool_calls = toolCalls },
                    finish_reason = "tool_calls"
                }
            },
            usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(responseBody), Encoding.UTF8, "application/json")
        };
    }
}
