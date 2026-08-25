using System.Globalization;
using BoundedExecution.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

var budget = new ExecutionBudget(
    MaxIterations: 5,
    MaxModelCalls: 5,
    MaxToolCalls: 10,
    MaxInputTokens: 20_000,
    MaxOutputTokens: 5_000,
    MaxElapsedTime: TimeSpan.FromSeconds(30),
    MaxEstimatedCost: 0.20m);
var state = new ExecutionBudgetState(budget);
var prices = TokenPrices.FromEnvironment();
var client = new BudgetedChatClient(Settings.ChatClient, state, prices);

var search = AIFunctionFactory.Create((string query) =>
    $"Possibly relevant result for '{query}'. More research may improve confidence.",
    "search", "Search for another possibly relevant source.");

var agent = new ChatClientAgent(client,
        "Research thoroughly. Use search repeatedly while another source might improve the answer. " +
        "When stopped, summarize only what you established and clearly label it incomplete.",
        tools: [search])
    .AsBuilder()
    .Use(async (messages, session, runOptions, next, cancellationToken) =>
    {
        state.RecordIteration();
        await next(messages, session, runOptions, cancellationToken);
    })
    .Use(async (_, context, next, cancellationToken) =>
    {
        state.RecordToolCall(context.Function.Name, perToolLimit: 8);
        return await next(context, cancellationToken);
    })
    .Build();

BoundedRunResult result;
var callerCancellation = CancellationToken.None;
using var timeout = state.CreateTimeout(callerCancellation);
try
{
    var answer = await agent.RunAsync("Research this topic until you have enough information: safe autonomous agents.",
        cancellationToken: timeout.Token);
    result = new BoundedRunResult(RunStatus.Complete, answer.Text, null, state.Snapshot());
}
catch (BudgetExceededException ex)
{
    result = new BoundedRunResult(RunStatus.Partial,
        "Research stopped at a hard execution boundary; any collected result is incomplete.", ex.Reason, ex.Snapshot);
}
catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested && timeout.IsCancellationRequested)
{
    result = new BoundedRunResult(RunStatus.Partial,
        "Research stopped at the elapsed-time boundary; any collected result is incomplete.",
        StopReason.ElapsedTimeLimitReached, state.Snapshot());
}

Console.WriteLine($"Result status: {result.Status}");
Console.WriteLine($"Stop reason: {result.StopReason?.ToString() ?? "None"}");
Console.WriteLine($"Model calls: {result.Budget.ModelCalls} / {budget.MaxModelCalls}");
Console.WriteLine($"Tool calls: {result.Budget.ToolCalls} / {budget.MaxToolCalls}");
Console.WriteLine($"Tokens in/out: {result.Budget.InputTokens}/{result.Budget.OutputTokens}");
Console.WriteLine($"Elapsed: {result.Budget.Elapsed.TotalSeconds:F1}s / {budget.MaxElapsedTime.TotalSeconds:F0}s");
Console.WriteLine($"Estimated cost: {result.Budget.EstimatedCost:C} / {budget.MaxEstimatedCost:C}");
Console.WriteLine($"Soft threshold reached: {result.Budget.SoftThresholdReached}");
Console.WriteLine($"Answer: {result.Answer}");

internal sealed record TokenPrices(decimal InputPerMillion, decimal OutputPerMillion)
{
    public static TokenPrices FromEnvironment() => new(
        Read("BOUNDED_EXECUTION_INPUT_COST_PER_MILLION", 2.50m),
        Read("BOUNDED_EXECUTION_OUTPUT_COST_PER_MILLION", 10m));

    public decimal Estimate(long inputTokens, long outputTokens) =>
        inputTokens / 1_000_000m * InputPerMillion + outputTokens / 1_000_000m * OutputPerMillion;

    private static decimal Read(string name, decimal fallback) =>
        decimal.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

internal sealed class BudgetedChatClient(IChatClient inner, ExecutionBudgetState state, TokenPrices prices)
    : DelegatingChatClient(inner)
{
    private const long ReservationFloor = 256;
    private const int DefaultOutputCap = 800;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages as IList<ChatMessage> ?? [.. messages];

        // ponytail: 4 chars per token is a coarse estimate. Swap for the provider's tokenizer
        // (Microsoft.ML.Tokenizers) when the input ceiling has to be exact rather than conservative.
        var estimatedInput = Math.Max(ReservationFloor,
            request.Sum(m => m.Text?.Length ?? 0) / 4 + request.Count * 8);

        // Cap the provider's own output so the worst case we reserve is the worst case that can
        // happen. Without this the output ceiling is advisory.
        var cap = Math.Min(options?.MaxOutputTokens ?? DefaultOutputCap, state.RemainingOutputTokens);
        if (cap <= 0) throw new BudgetExceededException(StopReason.OutputTokenLimitReached, state.Snapshot());
        options = options?.Clone() ?? new ChatOptions();
        options.MaxOutputTokens = (int)cap;

        var reservation = state.ReserveModelCall(estimatedInput, cap, prices.Estimate(estimatedInput, cap));
        try
        {
            var response = await base.GetResponseAsync(request, options, cancellationToken);
            state.Reconcile(reservation, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount,
                prices.Estimate);
            return response;
        }
        catch (BudgetExceededException) { throw; }
        catch
        {
            state.Release(reservation);
            throw;
        }
    }
}
