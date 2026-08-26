using ExceptionHandlingAndRecovery.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgenticPatterns.Tests;

public class RetryTests
{
    private static readonly Func<int, Task> NoBackoff = _ => Task.CompletedTask;

    private static AgentResponse ResponseWithToolResult(object? result, Exception? exception = null) =>
        new([new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call-1", result) { Exception = exception }])]);

    [Fact]
    public async Task FinalAttemptToolError_ReturnsNoResponse_SoCallerFallsBack()
    {
        var (response, error) = await Retry.RunAsync(
            _ => Task.FromResult(ResponseWithToolResult("Error: service down")),
            maxRetries: 3, NoBackoff);

        Assert.Null(response); // never "success" for a persistent tool failure
        Assert.NotNull(error);
    }

    [Fact]
    public async Task AllAttemptsThrow_ReturnsLastError()
    {
        var (response, error) = await Retry.RunAsync(
            _ => Task.FromException<AgentResponse>(new HttpRequestException("boom")),
            maxRetries: 2, NoBackoff);

        Assert.Null(response);
        Assert.IsType<HttpRequestException>(error);
    }

    [Fact]
    public async Task CleanFirstAttempt_ReturnsResponse()
    {
        var clean = ResponseWithToolResult("52.1, 12.5");

        var (response, error) = await Retry.RunAsync(
            _ => Task.FromResult(clean), maxRetries: 3, NoBackoff);

        Assert.Same(clean, response);
        Assert.Null(error);
    }

    [Fact]
    public async Task RecoveryOnSecondAttempt_ReturnsResponse()
    {
        var attempts = 0;

        var (response, _) = await Retry.RunAsync(
            _ => Task.FromResult(++attempts == 1
                ? ResponseWithToolResult("Error: transient")
                : ResponseWithToolResult("ok")),
            maxRetries: 3, NoBackoff);

        Assert.NotNull(response);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CallerCancellationIsNotTurnedIntoAFallback()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Retry.RunAsync(_ => Task.FromCanceled<AgentResponse>(cts.Token), maxRetries: 3, NoBackoff));
    }

    [Fact]
    public void HasToolError_DetectsExceptionAndErrorString()
    {
        Assert.True(Retry.HasToolError(ResponseWithToolResult(null, new InvalidOperationException())));
        Assert.True(Retry.HasToolError(ResponseWithToolResult("Error: nope")));
        Assert.False(Retry.HasToolError(ResponseWithToolResult("all good")));
    }
}
