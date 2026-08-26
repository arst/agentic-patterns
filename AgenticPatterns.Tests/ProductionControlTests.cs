using BoundedExecution.AgentFramework;
using IdempotentToolCalls.AgentFramework;
using Microsoft.Extensions.AI;
using OrchestratorWorkers.AgentFramework;
using ToolAuthorization.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class BoundedExecutionTests
{
    private static ExecutionBudget Budget(
        int iterations = 2, int modelCalls = 2, int toolCalls = 2, long inputTokens = 100, long outputTokens = 100,
        TimeSpan? elapsed = null, decimal cost = 10m) =>
        new(iterations, modelCalls, toolCalls, inputTokens, outputTokens, elapsed ?? TimeSpan.FromSeconds(10), cost);

    [Fact]
    public void ModelCallsAndRetriesCountAgainstTheLimit()
    {
        var state = new ExecutionBudgetState(Budget(modelCalls: 1));
        state.Release(state.ReserveModelCall(10, 10, 1)); // failed attempt still counts

        var error = Assert.Throws<BudgetExceededException>(() => state.ReserveModelCall(10, 10, 1));
        Assert.Equal(StopReason.ModelCallLimitReached, error.Reason);
        Assert.Equal(1, state.ModelCalls);
    }

    [Fact]
    public void ToolCallsHonorTotalAndPerToolLimits()
    {
        var state = new ExecutionBudgetState(Budget(toolCalls: 3));
        state.RecordToolCall("search", perToolLimit: 1);

        var error = Assert.Throws<BudgetExceededException>(() => state.RecordToolCall("search", perToolLimit: 1));
        Assert.Equal(StopReason.ToolCallLimitReached, error.Reason);
    }

    [Fact]
    public void ReconciliationRecordsActualTokensAndCost()
    {
        var state = new ExecutionBudgetState(Budget());
        var reservation = state.ReserveModelCall(50, 30, 2);
        state.Reconcile(reservation, 12, 7, (_, _) => 0.25m);

        var snapshot = state.Snapshot();
        Assert.Equal(12, snapshot.InputTokens);
        Assert.Equal(7, snapshot.OutputTokens);
        Assert.Equal(0.25m, snapshot.EstimatedCost);
    }

    [Fact]
    public void IterationsAreCountedSeparatelyFromModelCalls()
    {
        var state = new ExecutionBudgetState(Budget(iterations: 2, modelCalls: 10));
        state.RecordIteration();
        state.ReserveModelCall(1, 1, 0m);
        state.ReserveModelCall(1, 1, 0m);
        Assert.Equal(1, state.Snapshot().Iterations);
        Assert.Equal(2, state.Snapshot().ModelCalls);
    }

    [Fact]
    public void MissingUsageIsChargedAtTheReservation()
    {
        var state = new ExecutionBudgetState(Budget(outputTokens: 1_000));
        var reservation = state.ReserveModelCall(10, 800, 0m);
        state.Reconcile(reservation, inputTokens: null, outputTokens: null, (_, _) => 0m);
        Assert.Equal(800, state.Snapshot().OutputTokens);
    }

    [Fact]
    public void AReservationLargerThanTheRemainingBudgetThrowsBeforeTheCall()
    {
        var state = new ExecutionBudgetState(Budget(outputTokens: 500));
        Assert.Throws<BudgetExceededException>(() => state.ReserveModelCall(10, 800, 0m));
    }

    [Fact]
    public void TimeoutUsesTheRemainingDurationNotTheWholeBudget()
    {
        var state = new ExecutionBudgetState(Budget(elapsed: TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);
        Assert.True(state.RemainingTime < TimeSpan.FromSeconds(10),
            "CreateTimeout must schedule the remaining duration, not restart the full budget");
    }

    [Fact]
    public async Task ConcurrentReservationsCannotCollectivelyOverspend()
    {
        var state = new ExecutionBudgetState(Budget(modelCalls: 4, inputTokens: 100));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { return state.ReserveModelCall(60, 10, 1); }
            catch (BudgetExceededException) { return null; }
        })));

        Assert.Single(attempts, r => r is not null);
        foreach (var reservation in attempts.OfType<ModelCallReservation>()) state.Release(reservation);
    }

    [Fact]
    public void ElapsedTimeIsAHardLimit()
    {
        var state = new ExecutionBudgetState(Budget(elapsed: TimeSpan.FromMilliseconds(1)));
        Thread.Sleep(5);
        Assert.Equal(StopReason.ElapsedTimeLimitReached,
            Assert.Throws<BudgetExceededException>(() => state.RecordToolCall("search")).Reason);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        var state = new ExecutionBudgetState(Budget());
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        using var timeout = state.CreateTimeout(caller.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(1, timeout.Token));
    }

    [Fact]
    public void StoppedResultIsExplicitlyPartial()
    {
        var state = new ExecutionBudgetState(Budget());
        var result = new BoundedRunResult(RunStatus.Partial, "incomplete", StopReason.ToolCallLimitReached,
            state.Snapshot());
        Assert.Equal(RunStatus.Partial, result.Status);
        Assert.NotNull(result.StopReason);
    }
}

public class ToolAuthorizationTests
{
    private static readonly RunPrincipal Principal = new("CUSTOMER-100", "TENANT-EU");
    private static readonly Dictionary<string, RunPrincipal> Owners = new()
    {
        ["ORD-100"] = Principal,
        ["ORD-OTHER-TENANT"] = new("CUSTOMER-100", "OTHER")
    };

    private static ToolCapability Grant(string tool, decimal? maximum = null,
        DateTimeOffset? expires = null, IReadOnlyDictionary<string, string>? constraints = null,
        bool oneTime = false) => new(Principal.SubjectId, Principal.TenantId, tool,
        constraints ?? new Dictionary<string, string>(), maximum, expires ?? DateTimeOffset.UtcNow.AddMinutes(1),
        Guid.NewGuid().ToString("N"), oneTime);

    [Fact]
    public void CrossTenantInvocationIsDenied()
    {
        var decision = new ToolAuthorizationPolicy(Owners).Authorize(
            new RunPrincipal(Principal.SubjectId, "OTHER"), Grant("GetOrder"), "GetOrder",
            new AIFunctionArguments { ["orderId"] = "ORD-100" });
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void AmountBeyondCapabilityRequiresApproval()
    {
        var decision = new ToolAuthorizationPolicy(Owners).Authorize(Principal, Grant("IssueRefund", 50m),
            "IssueRefund", new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = 500m });
        Assert.Equal(AuthorizationOutcome.ApprovalRequired, decision.Outcome);
    }

    [Fact]
    public void MissingMalformedAndCrossTenantResourceArgumentsAreDenied()
    {
        var policy = new ToolAuthorizationPolicy(Owners);
        Assert.Equal(AuthorizationOutcome.Denied,
            policy.Authorize(Principal, Grant("GetOrder"), "GetOrder", []).Outcome);
        Assert.Equal(AuthorizationOutcome.Denied,
            policy.Authorize(Principal, Grant("IssueRefund", 50m), "IssueRefund",
                new AIFunctionArguments { ["orderId"] = "ORD-100", ["amount"] = "not-money" }).Outcome);
        Assert.Equal(AuthorizationOutcome.Denied,
            policy.Authorize(Principal, Grant("GetOrder"), "GetOrder",
                new AIFunctionArguments { ["orderId"] = "ORD-OTHER-TENANT" }).Outcome);
    }

    [Fact]
    public void ExpiredAndWrongToolGrantsAreDenied()
    {
        var policy = new ToolAuthorizationPolicy(Owners);
        Assert.Equal(AuthorizationOutcome.Denied,
            policy.Authorize(Principal, Grant("GetOrder", expires: DateTimeOffset.UtcNow.AddSeconds(-1)),
                "GetOrder", new AIFunctionArguments()).Outcome);
        Assert.Equal(AuthorizationOutcome.Denied,
            policy.Authorize(Principal, Grant("GetOrder"), "IssueRefund", new AIFunctionArguments()).Outcome);
    }

    [Fact]
    public void CapabilityScopeIsCopiedAndCannotBeExpandedByCaller()
    {
        var constraints = new Dictionary<string, string> { ["orderId"] = "ORD-100" };
        var capability = Grant("GetOrder", constraints: constraints);
        constraints["orderId"] = "ORD-999";

        Assert.Equal(AuthorizationOutcome.Allowed,
            new ToolAuthorizationPolicy(Owners).Authorize(Principal, capability, "GetOrder",
                new AIFunctionArguments { ["orderId"] = "ORD-100" }).Outcome);
    }

    [Fact]
    public void OneTimeGrantCannotBeReplayed()
    {
        var policy = new ToolAuthorizationPolicy(Owners);
        var capability = Grant("GetOrder", oneTime: true);
        var args = new AIFunctionArguments { ["orderId"] = "ORD-100" };
        Assert.Equal(AuthorizationOutcome.Allowed, policy.Authorize(Principal, capability, "GetOrder", args).Outcome);
        Assert.Equal(AuthorizationOutcome.Denied, policy.Authorize(Principal, capability, "GetOrder", args).Outcome);
    }
}

public class IdempotentToolCallTests
{
    [Fact]
    public async Task RefundServiceDeduplicatesAcrossACallerCrash()
    {
        var service = new SimulatedRefundService();
        var key = "key-1";

        // Attempt 1: the service commits the refund and then the response is lost.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.IssueRefundAsync("tenant-a", key, "ORD-100", 25m,
                loseResponseAfterCommit: true, CancellationToken.None));

        // The caller process died: it kept no local state at all. A brand new tool
        // instance retries with the same key.
        var tool = new IdempotentTool(service);
        var refund = await tool.IssueRefundAsync("ORD-100", 25m, key);

        Assert.Single(service.Refunds);
        Assert.Equal(service.Refunds.Single().Id, refund.Id);
    }

    [Fact]
    public async Task ConcurrentCallsWithTheSameKeyProduceExactlyOneRefund()
    {
        var service = new SimulatedRefundService();
        var key = "key-concurrent";

        var refunds = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            service.IssueRefundAsync("tenant-a", key, "ORD-100", 25m,
                loseResponseAfterCommit: false, CancellationToken.None)));

        Assert.Single(service.Refunds);
        Assert.All(refunds, r => Assert.Equal(service.Refunds.Single().Id, r.Id));
    }

    [Fact]
    public async Task CallerCancellationSurfacesAsCancellationNotAFailure()
    {
        var service = new SimulatedRefundService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.IssueRefundAsync("tenant-a", "key-cancel", "ORD-100", 25m,
                loseResponseAfterCommit: false, cts.Token));
    }

    [Fact]
    public async Task RefundServiceRejectsTheSameKeyForADifferentRequest()
    {
        var service = new SimulatedRefundService();
        await service.IssueRefundAsync("tenant-a", "key-1", "ORD-100", 25m, false, CancellationToken.None);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            service.IssueRefundAsync("tenant-a", "key-1", "ORD-100", 30m, false, CancellationToken.None));
    }

    [Fact]
    public async Task RefundKeysAreScopedPerTenant()
    {
        var service = new SimulatedRefundService();
        await service.IssueRefundAsync("tenant-a", "key-1", "ORD-100", 25m, false, CancellationToken.None);
        await service.IssueRefundAsync("tenant-b", "key-1", "ORD-200", 25m, false, CancellationToken.None);
        Assert.Equal(2, service.Refunds.Count);
    }
}

public class OrchestratorWorkerTests
{
    [Fact]
    public void ValidatorRejectsUnknownRolesTooManyTasksAndDuplicateIds()
    {
        var plan = new WorkPlan([
            new("same", "Unknown", "do work"),
            new("same", "Allowed", "do work"),
            new("three", "Allowed", "do work")
        ]);
        var errors = PlanValidator.Validate(plan, new HashSet<string> { "Allowed" }, maximumTasks: 2);
        Assert.Contains(errors, e => e.Contains("maximum"));
        Assert.Contains(errors, e => e.Contains("not allowed"));
        Assert.Contains(errors, e => e.Contains("Duplicate"));
    }

    [Fact]
    public async Task ExecutionHonorsConcurrencyAndPreservesPartialSuccess()
    {
        var current = 0;
        var maximum = 0;
        var registry = new WorkerRegistry();
        registry.Register("Worker", async (task, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref current);
            maximum = Math.Max(maximum, active);
            try
            {
                await Task.Delay(20, cancellationToken);
                if (task.Id == "bad") throw new InvalidOperationException("simulated failure");
                return task.Id;
            }
            finally { Interlocked.Decrement(ref current); }
        });
        var results = await registry.ExecuteAsync(new WorkPlan([
            new("one", "Worker", "work"), new("bad", "Worker", "work"), new("three", "Worker", "work")
        ]), maximumConcurrency: 2);

        Assert.True(maximum <= 2);
        Assert.Equal(2, results.Count(r => r.Succeeded));
        Assert.Single(results, r => !r.Succeeded);
        var synthesis = WorkerRegistry.BuildSynthesisInput(results);
        Assert.Contains("one", synthesis);
        Assert.Contains("three", synthesis);
        Assert.DoesNotContain("simulated failure", synthesis);
    }
}

public class GuardRailsTests
{
    [Fact]
    public void RedactionPreservesFunctionCallsAndUsage()
    {
        var response = GuardRails.Redact(new ChatResponse(
            [new ChatMessage(ChatRole.Assistant,
                [new TextContent("Call me at 555-867-5309."), new FunctionCallContent("c1", "lookup", null)])])
            { FinishReason = ChatFinishReason.Stop, Usage = new UsageDetails { OutputTokenCount = 12 } });

        // SafetyChecks.RedactPii tags matches by field ("[Phone_REDACTED]"), not a generic
        // "[redacted]" placeholder, so assert on the field-agnostic part of the tag — and assert
        // the secret itself is gone, not just that some tag is present somewhere.
        Assert.Contains("REDACTED", response.Text);
        Assert.DoesNotContain("555-867-5309", response.Text);
        Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(12, response.Usage?.OutputTokenCount);
    }

    [Fact]
    public void RedactionLeavesCleanTextAndNonTextContentAlone()
    {
        var functionResult = new FunctionResultContent("c1", "no PII here");
        var response = GuardRails.Redact(new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, [new TextContent("Nothing sensitive."), functionResult])]));

        Assert.Equal("Nothing sensitive.", response.Text);
        Assert.Same(functionResult, response.Messages[0].Contents[1]);
    }

    [Fact]
    public void RedactionCopiesResponseMetadata()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var original = new ChatResponse([new ChatMessage(ChatRole.Assistant, "clean text")])
        {
            ModelId = "gpt-test",
            ResponseId = "resp-1",
            CreatedAt = createdAt,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["k"] = "v" }
        };

        var redacted = GuardRails.Redact(original);

        Assert.Equal("gpt-test", redacted.ModelId);
        Assert.Equal("resp-1", redacted.ResponseId);
        Assert.Equal(createdAt, redacted.CreatedAt);
        Assert.Equal("v", redacted.AdditionalProperties?["k"]);
    }

    [Fact]
    public void TruncateTrimsOnlyTheLastTextContent()
    {
        var functionCall = new FunctionCallContent("c1", "lookup", null);
        var response = new ChatResponse([
            new ChatMessage(ChatRole.Assistant, [new TextContent("first ten.")]),
            new ChatMessage(ChatRole.Assistant, [functionCall, new TextContent("second block of text")])
        ]);

        // 10 ("first ten.") + budget 5 left for the last TextContent out of a 15-character cap.
        var truncated = GuardRails.Truncate(response, maxCharacters: 15);

        Assert.Equal("first ten.", ((TextContent)truncated.Messages[0].Contents[0]).Text);
        Assert.Same(functionCall, truncated.Messages[1].Contents[0]);
        var lastText = ((TextContent)truncated.Messages[1].Contents[1]).Text;
        Assert.StartsWith("secon", lastText);
        Assert.Contains("[Response truncated for safety.]", lastText);
    }

    [Fact]
    public void TruncateIsANoOpUnderTheLimit()
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "short")]);
        var truncated = GuardRails.Truncate(response, maxCharacters: 2000);
        Assert.Same(response.Messages, truncated.Messages);
    }
}
