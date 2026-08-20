using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace EvaluationAndMonitoring.AgentFramework;

public enum TracePrivacyMode { FullContent, RedactedContent, HashesOnly }

public sealed class RunTrace(string promptVersion, TracePrivacyMode privacyMode = TracePrivacyMode.FullContent)
{
    public string PromptVersion { get; init; } = promptVersion;
    public TracePrivacyMode PrivacyMode { get; init; } = privacyMode;
    public string StopReason { get; set; } = "Running";
    public List<RecordedModelCall> ModelCalls { get; init; } = [];
    public List<RecordedToolCall> ToolCalls { get; init; } = [];
}

public sealed record CapturedValue(string Hash, string? Value);
public sealed record RecordedContent(string Kind, CapturedValue Payload, string? CallId = null, string? Name = null);
public sealed record RecordedMessage(string Role, IReadOnlyList<RecordedContent> Contents);
public sealed record RecordedModelCall(
    string RequestHash,
    IReadOnlyList<RecordedMessage> Messages,
    string GenerationOptions,
    IReadOnlyList<RecordedMessage> ResponseMessages,
    string ModelId,
    string? FinishReason,
    long InputTokens,
    long OutputTokens);
public sealed record RecordedToolCall(
    string Name,
    CapturedValue Arguments,
    CapturedValue Result,
    bool Succeeded,
    string? ErrorType = null);

public sealed class RecordingChatClient(IChatClient inner, RunTrace trace) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        var response = await base.GetResponseAsync(request, options, cancellationToken);
        trace.ModelCalls.Add(new RecordedModelCall(
            TraceStore.HashMessages(request, options, trace.PrivacyMode),
            TraceStore.CaptureMessages(request, trace.PrivacyMode),
            TraceStore.CanonicalOptions(options),
            TraceStore.CaptureMessages(response.Messages, trace.PrivacyMode),
            response.ModelId ?? options?.ModelId ?? "unknown",
            response.FinishReason?.Value,
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0));
        return response;
    }
}

public sealed class ReplayChatClient : IChatClient
{
    private readonly RunTrace _trace;
    private int _next;

    public ReplayChatClient(RunTrace trace)
    {
        if (trace.PrivacyMode == TracePrivacyMode.HashesOnly)
            throw new InvalidOperationException("Hash-only traces prove equality but cannot replay outputs.");
        _trace = trace;
    }

    public int RemainingCalls => _trace.ModelCalls.Count - _next;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_next >= _trace.ModelCalls.Count)
            throw new InvalidOperationException("Replay requested more model calls than the trace contains.");

        var call = _trace.ModelCalls[_next++];
        var actualHash = TraceStore.HashMessages(messages, options, _trace.PrivacyMode);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(call.RequestHash), Convert.FromHexString(actualHash)))
            throw new InvalidOperationException($"Replay diverged at model call {_next}: request hash changed.");

        return Task.FromResult(new ChatResponse([.. TraceStore.RestoreMessages(call.ResponseMessages)])
        {
            ModelId = call.ModelId,
            FinishReason = call.FinishReason is null ? null : new ChatFinishReason(call.FinishReason),
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

public sealed class ToolTraceSession(RunTrace trace, bool replay)
{
    // ponytail: replay is sequential; add correlation IDs before supporting parallel tool fan-out.
    private int _next;
    public int RemainingCalls => trace.ToolCalls.Count - _next;

    public async ValueTask<object?> InvokeAsync(AIFunction inner, AIFunctionArguments arguments,
        Func<ValueTask<object?>> invokeLive)
    {
        var argumentsJson = JsonSerializer.Serialize(arguments.OrderBy(a => a.Key)
            .ToDictionary(a => a.Key, a => a.Value));
        var capturedArguments = TraceStore.Capture(argumentsJson, trace.PrivacyMode);

        if (replay)
        {
            if (trace.PrivacyMode == TracePrivacyMode.HashesOnly)
                throw new InvalidOperationException("Hash-only traces cannot replay tool results.");
            if (_next >= trace.ToolCalls.Count)
                throw new InvalidOperationException("Replay requested more tool calls than the trace contains.");
            var recorded = trace.ToolCalls[_next++];
            if (recorded.Name != inner.Name || recorded.Arguments.Hash != capturedArguments.Hash)
                throw new InvalidOperationException($"Replay diverged at tool call {_next}.");
            if (!recorded.Succeeded)
                throw new RecordedToolException(recorded.ErrorType ?? "Unknown", recorded.Result.Value ?? "Recorded failure");
            return JsonSerializer.Deserialize<object?>(recorded.Result.Value!);
        }

        try
        {
            var result = await invokeLive();
            trace.ToolCalls.Add(new RecordedToolCall(inner.Name, capturedArguments,
                TraceStore.Capture(JsonSerializer.Serialize(result), trace.PrivacyMode), true));
            return result;
        }
        catch (Exception ex)
        {
            trace.ToolCalls.Add(new RecordedToolCall(inner.Name, capturedArguments,
                TraceStore.Capture(ex.Message, trace.PrivacyMode), false, ex.GetType().FullName));
            throw;
        }
    }
}

public sealed class RecordedAIFunction(AIFunction inner, ToolTraceSession session) : DelegatingAIFunction(inner)
{
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken) =>
        session.InvokeAsync(this, arguments, () => base.InvokeCoreAsync(arguments, cancellationToken));
}

public sealed class RecordedToolException(string errorType, string message) : Exception(message)
{
    public string ErrorType { get; } = errorType;
}

public static class TraceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task SaveAsync(string path, RunTrace trace, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(trace, Json), cancellationToken);

    public static async Task<RunTrace> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        JsonSerializer.Deserialize<RunTrace>(await File.ReadAllTextAsync(path, cancellationToken), Json)
        ?? throw new InvalidDataException("Trace file is empty or invalid.");

    public static CapturedValue Capture(string value, TracePrivacyMode mode)
    {
        var protectedValue = mode == TracePrivacyMode.RedactedContent ? Redact(value) : value;
        return new CapturedValue(Hash(protectedValue), mode == TracePrivacyMode.HashesOnly ? null : protectedValue);
    }

    public static IReadOnlyList<RecordedMessage> CaptureMessages(IEnumerable<ChatMessage> messages,
        TracePrivacyMode mode) => messages.Select(message => new RecordedMessage(message.Role.Value,
        message.Contents.Select(content => content switch
        {
            TextContent text => new RecordedContent("text", Capture(text.Text, mode)),
            FunctionCallContent call => new RecordedContent("function-call",
                Capture(JsonSerializer.Serialize(call.Arguments?.OrderBy(a => a.Key)
                    .ToDictionary(a => a.Key, a => a.Value)), mode), call.CallId, call.Name),
            FunctionResultContent result => new RecordedContent("function-result",
                Capture(JsonSerializer.Serialize(result.Result), mode), result.CallId),
            _ => new RecordedContent("other", Capture(content.ToString() ?? "", mode))
        }).ToArray())).ToArray();

    public static IReadOnlyList<ChatMessage> RestoreMessages(IEnumerable<RecordedMessage> messages) =>
        messages.Select(message => new ChatMessage(new ChatRole(message.Role), message.Contents.Select(content =>
            content.Kind switch
            {
                "text" => (AIContent)new TextContent(content.Payload.Value!),
                "function-call" => new FunctionCallContent(content.CallId!, content.Name!,
                    JsonSerializer.Deserialize<Dictionary<string, object?>>(content.Payload.Value!) ?? []),
                "function-result" => new FunctionResultContent(content.CallId!,
                    JsonSerializer.Deserialize<object?>(content.Payload.Value!)),
                _ => new TextContent(content.Payload.Value!)
            }).ToList())).ToArray();

    public static string HashMessages(IEnumerable<ChatMessage> messages, ChatOptions? options,
        TracePrivacyMode mode)
    {
        var canonical = JsonSerializer.Serialize(CaptureMessages(messages, mode), Json) + CanonicalOptions(options);
        return Hash(canonical);
    }

    public static string CanonicalOptions(ChatOptions? options)
    {
        var tools = string.Join(";", options?.Tools?.Select(tool => tool is AIFunctionDeclaration function
            ? $"{function.Name}:{function.JsonSchema.GetRawText()}"
            : $"{tool.Name}:{tool.Description}") ?? []);
        return $"model:{options?.ModelId}|temperature:{options?.Temperature}|format:{options?.ResponseFormat}|tools:{tools}";
    }

    private static string Redact(string value) => Regex.Replace(
        Regex.Replace(value, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[REDACTED_EMAIL]"),
        @"(?i)(bearer\s+|api[_-]?key[\""'= :]+|sk-)[A-Za-z0-9._-]+", "$1[REDACTED_SECRET]");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
