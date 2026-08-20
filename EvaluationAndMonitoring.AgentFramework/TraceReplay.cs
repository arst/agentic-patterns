using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace EvaluationAndMonitoring.AgentFramework;

public sealed class RunTrace(string promptVersion)
{
    public string PromptVersion { get; init; } = promptVersion;
    public string StopReason { get; set; } = "Running";
    public List<RecordedModelCall> ModelCalls { get; init; } = [];
}

public sealed record RecordedMessage(string Role, string Text);

public sealed record RecordedModelCall(
    string RequestHash,
    IReadOnlyList<RecordedMessage> Messages,
    string GenerationOptions,
    string Response,
    string ModelId,
    long InputTokens,
    long OutputTokens);

public sealed class RecordingChatClient(IChatClient inner, RunTrace trace) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        var response = await base.GetResponseAsync(request, options, cancellationToken);
        trace.ModelCalls.Add(new RecordedModelCall(
            TraceStore.Hash(request, options),
            request.Select(m => new RecordedMessage(m.Role.Value, m.Text ?? "")).ToArray(),
            TraceStore.CanonicalOptions(options),
            string.Concat(response.Messages.Select(m => m.Text)),
            response.ModelId ?? options?.ModelId ?? "unknown",
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0));
        return response;
    }
}

public sealed class ReplayChatClient(RunTrace trace) : IChatClient
{
    private int _next;

    public int RemainingCalls => trace.ModelCalls.Count - _next;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_next >= trace.ModelCalls.Count)
            throw new InvalidOperationException("Replay requested more model calls than the trace contains.");

        var call = trace.ModelCalls[_next++];
        var actualHash = TraceStore.Hash(messages, options);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(call.RequestHash), Convert.FromHexString(actualHash)))
            throw new InvalidOperationException($"Replay diverged at model call {_next}: request hash changed.");

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, call.Response))
        {
            ModelId = call.ModelId,
            Usage = new UsageDetails
            {
                InputTokenCount = call.InputTokens,
                OutputTokenCount = call.OutputTokens,
                TotalTokenCount = call.InputTokens + call.OutputTokens
            }
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This sample records and replays non-streaming model calls.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public static class TraceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task SaveAsync(string path, RunTrace trace, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(trace, Json), cancellationToken);

    public static async Task<RunTrace> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        JsonSerializer.Deserialize<RunTrace>(await File.ReadAllTextAsync(path, cancellationToken), Json)
        ?? throw new InvalidDataException("Trace file is empty or invalid.");

    public static string Hash(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var canonical = string.Join("\n", messages.Select(m => $"{m.Role.Value}:{m.Text}")) +
                        $"\n{CanonicalOptions(options)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CanonicalOptions(ChatOptions? options) =>
        $"model:{options?.ModelId}|temperature:{options?.Temperature}|format:{options?.ResponseFormat}";
}
