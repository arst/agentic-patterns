using EvaluationAndMonitoring.AgentFramework;
using ExceptionHandlingAndRecovery.AgentFramework;
using MemoryManagement.AgentFramework;
using Microsoft.Extensions.AI;
using SelfCorrectionLoop.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class EvaluatorOptimizerTests
{
    private static readonly Evaluation Approved = new(true, 1.2,
        [new CriterionResult("Clarity", true, "Clear.")], "Looks good.");

    [Fact]
    public void HostChecksOverrideModelApprovalAndClampScore()
    {
        var result = HostEvaluation.Apply("Guaranteed green products", Approved, 20,
            "GreenTech Gadgets", ["guaranteed"]);

        Assert.False(result.Approved);
        Assert.Equal(1, result.Score);
        Assert.Contains(result.Criteria, c => c.Name == "Character limit" && !c.Passed);
        Assert.Contains(result.Criteria, c => c.Name == "Required product name" && !c.Passed);
        Assert.Contains(result.Criteria, c => c.Name == "Forbidden terms" && !c.Passed);
    }

    [Fact]
    public void ModelAndHostMustBothApprove()
    {
        var result = HostEvaluation.Apply("Try GreenTech Gadgets today!", Approved, 150,
            "GreenTech Gadgets", ["guaranteed"]);
        Assert.True(result.Approved);
    }
}

public class TraceReplayTests
{
    [Fact]
    public async Task RecordedModelOutputReplaysWithoutCallingLiveClient()
    {
        var trace = new RunTrace("v1");
        using var recorder = new RecordingChatClient(
            new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "recorded answer"))
            {
                ModelId = "test-model"
            }), trace);
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        await recorder.GetResponseAsync(messages);
        Assert.Contains("model:", trace.ModelCalls.Single().GenerationOptions);

        using var replay = new ReplayChatClient(trace);
        var response = await replay.GetResponseAsync(messages);

        Assert.Equal("recorded answer", response.Messages.Single().Text);
        Assert.Equal(0, replay.RemainingCalls);
    }

    [Fact]
    public async Task ReplayFailsWhenOrchestrationRequestChanges()
    {
        var original = new[] { new ChatMessage(ChatRole.User, "original") };
        var trace = new RunTrace("v1");
        using (var recorder = new RecordingChatClient(
                   new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"))), trace))
            await recorder.GetResponseAsync(original);

        using var replay = new ReplayChatClient(trace);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            replay.GetResponseAsync([new ChatMessage(ChatRole.User, "changed")]));
    }

    [Fact]
    public async Task TraceFileRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-trace-{Guid.NewGuid():N}.json");
        try
        {
            var trace = new RunTrace("v1") { StopReason = "Completed" };
            await TraceStore.SaveAsync(path, trace);
            var loaded = await TraceStore.LoadAsync(path);
            Assert.Equal("v1", loaded.PromptVersion);
            Assert.Equal("Completed", loaded.StopReason);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class MemoryIsolationTests
{
    [Fact]
    public void ConsentScopeTtlRestartAndDeleteAreEnforced()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var anna = new MemoryScope("TENANT-A", "ANNA");
        var otherTenant = new MemoryScope("TENANT-B", "ANNA");
        var store = new ScopedLongTermMemory(() => now);

        Assert.False(store.Remember(anna, "format", "PDF", TimeSpan.FromHours(1), consent: false));
        Assert.True(store.Remember(anna, "format", "PDF", TimeSpan.FromHours(1), consent: true));
        Assert.Null(store.Recall(otherTenant, "format"));

        var restarted = ScopedLongTermMemory.Deserialize(store.Serialize(), () => now);
        Assert.Equal("PDF", restarted.Recall(anna, "format"));
        now = now.AddHours(2);
        Assert.Null(restarted.Recall(anna, "format"));

        now = now.AddHours(1);
        restarted.Remember(anna, "format", "PDF", TimeSpan.FromHours(1), consent: true);
        Assert.Equal(1, restarted.Delete(anna));
        Assert.Null(restarted.Recall(anna, "format"));
    }
}

public class DependencyCircuitBreakerTests
{
    [Fact]
    public async Task OpensFailsFastAndClosesAfterSuccessfulHalfOpenProbe()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var calls = 0;
        var breaker = new DependencyCircuitBreaker(2, TimeSpan.FromMinutes(1), () => now);
        Task<string> Fail(CancellationToken _) { calls++; throw new HttpRequestException("503"); }

        await Assert.ThrowsAsync<HttpRequestException>(() => breaker.ExecuteAsync(Fail));
        await Assert.ThrowsAsync<HttpRequestException>(() => breaker.ExecuteAsync(Fail));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => breaker.ExecuteAsync(Fail));
        Assert.Equal(2, calls);
        Assert.Equal(CircuitState.Open, breaker.State);

        now = now.AddMinutes(1);
        Assert.Equal("ok", await breaker.ExecuteAsync(_ => Task.FromResult("ok")));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task HalfOpenAllowsOnlyOneProbe()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var breaker = new DependencyCircuitBreaker(1, TimeSpan.FromMinutes(1), () => now);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            breaker.ExecuteAsync<string>(_ => throw new HttpRequestException("503")));
        now = now.AddMinutes(1);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = breaker.ExecuteAsync(async _ =>
        {
            started.SetResult();
            return await release.Task;
        });
        await started.Task;
        await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            breaker.ExecuteAsync(_ => Task.FromResult("second")));
        release.SetResult("probe-ok");
        Assert.Equal("probe-ok", await probe);
    }

    [Fact]
    public async Task PermanentErrorsAndCallerCancellationDoNotTripCircuit()
    {
        var breaker = new DependencyCircuitBreaker(1, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<string>(_ => throw new InvalidOperationException("validation")));

        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            breaker.ExecuteAsync<string>(ct => Task.FromCanceled<string>(ct), caller.Token));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }
}
