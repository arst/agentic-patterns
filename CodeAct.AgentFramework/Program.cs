using System.Text.Json;
using CodeAct.AgentFramework.Execution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// CodeAct: instead of one bound tool per action — where every intermediate result
// round-trips through the model's context — the agent gets a SINGLE tool, ExecuteCSharp,
// and a small action API it can script against. Loops, filtering, and aggregation happen
// inside the script; only what the script PRINTS ever enters the context.
// The same 20-order question is answered twice, classic tool-calling first, and the
// token usage of both runs is printed side by side.
//
// Model-generated code is UNTRUSTED code. It runs in a locked-down local container
// (no network, read-only rootfs, no capabilities, non-root, resource limits) and the
// sample FAILS CLOSED when no container runtime is available — see Execution/.

// Fail closed BEFORE any model call: no container runtime and no explicit double
// opt-in means the sample refuses to run rather than degrading to host execution.
var runner = CodeRunnerFactory.Create(new CodeExecutionOptions
{
    AllowUnsafeHostExecution = CodeRunnerFactory.IsUnsafeHostExecutionRequested(args)
});

const string question =
    "Across orders A-100 through A-119: which orders are delayed, and what is the total value " +
    "of the delayed orders? List the delayed order ids and the total value.";

Console.WriteLine($"Question: {question}\n");

// ---- Round 1: classic tool-calling (every bulky order payload enters the context) ----

Console.WriteLine("---- Round 1: classic tool-calling agent ----\n");

var classicMeter = new CallCounter(Settings.ChatClient);
var classicAgent = new ChatClientAgent(classicMeter,
    "You are an order analyst. Use the get_order tool to look up orders. Be concise.",
    tools: [AIFunctionFactory.Create((string orderId) => JsonSerializer.Serialize(MakeOrder(orderId)),
        "get_order", "Returns the full order record (status, value, carrier, scan history, notes) as JSON.")]);

var classicResponse = await classicAgent.RunAsync(question);
Console.WriteLine($"Agent: {classicResponse.Text}\n");
Report("classic", classicMeter, classicResponse);

// ---- Round 2: CodeAct agent (one tool, results stay inside the script) ----

Console.WriteLine("\n---- Round 2: CodeAct agent ----\n");

var codeActMeter = new CallCounter(Settings.ChatClient);
var codeActAgent = new ChatClientAgent(codeActMeter,
    """
    You solve tasks by writing C# and running it with the execute_csharp tool.
    Already defined inside your script (do NOT redefine them):
        Order GetOrder(string orderId)   // e.g. GetOrder("A-101")
        record Order(string Id, string Status, decimal Total, string Carrier, string[] History, string Notes)
        // Status is exactly "delayed" or "in transit" (lowercase).
    Write TOP-LEVEL STATEMENTS ONLY — no classes, no Main, no #: directives.
    Chain calls with normal loops and variables, and Console.WriteLine ONLY the final
    findings. Then answer the user from the script output.
    """,
    tools: [AIFunctionFactory.Create(ExecuteCSharp,
        "execute_csharp", "Compiles and runs the given C# top-level statements; returns the program's console output.")]);

var codeActResponse = await codeActAgent.RunAsync(question);
Console.WriteLine($"Agent: {codeActResponse.Text}\n");
Report("CodeAct", codeActMeter, codeActResponse);

return;

async Task<string> ExecuteCSharp(string code)
{
    // Models sometimes wrap code in markdown fences despite instructions.
    code = code.Trim();
    if (code.StartsWith("```"))
        code = string.Join('\n', code.Split('\n')[1..^1]);

    // Hard guard, not just a prompt instruction: strip file-based-app directives so the
    // model cannot add package references or SDK switches (`#:package`, `#:sdk`, ...).
    // The sandbox has no network anyway — generated programs get the BCL and nothing else.
    code = string.Join('\n', code.Split('\n').Where(l => !l.TrimStart().StartsWith("#:")));

    Console.WriteLine("  [agent script]");
    foreach (var line in code.Split('\n')) Console.WriteLine($"  | {line}");

    // The action API is appended below the model's code: local functions may follow the
    // top-level statements, and are callable from them regardless of declaration order.
    var execution = await runner.RunAsync(code + "\n\n" + ActionApiSource, CancellationToken.None);

    var result = execution switch
    {
        { TimedOut: true } => "Script exceeded the time limit and was killed.",
        { ExitCode: 0 } => execution.StandardOutput,
        _ => $"Script failed (exit {execution.ExitCode}):\n{execution.StandardError}"
    };
    if (result.Length > 4000) result = result[..4000] + "\n[truncated]";
    Console.WriteLine($"  [script output]\n  | {result.Trim().Replace("\n", "\n  | ")}\n");
    return result;
}

static void Report(string label, CallCounter meter, AgentResponse response) =>
    Console.WriteLine($"  [{label}: {meter.ModelCalls} model calls | " +
                      $"input tokens: {response.Usage?.InputTokenCount} | output tokens: {response.Usage?.OutputTokenCount}]");

// Deterministic fake order data. The same logic is duplicated in ActionApiSource below,
// so the classic tool and the script API return byte-identical payloads.
static Order MakeOrder(string orderId)
{
    var n = int.Parse(orderId[2..]);
    return new Order(orderId,
        n % 4 == 1 ? "delayed" : "in transit",
        40 + n * 3.5m,
        n % 2 == 0 ? "PostNord" : "GLS",
        [.. Enumerable.Range(0, 6).Select(i => $"2026-08-{10 + i:00}T0{i}:15:00Z scan at hub-{(n * 7 + i) % 50:00}")],
        "Signature required on delivery. Fragile items in parcel. Contact carrier for rerouting. Customs cleared at DK-CPH.");
}

internal record Order(string Id, string Status, decimal Total, string Carrier, string[] History, string Notes);

/// <summary>
/// Delegating IChatClient that counts model round-trips, so the demo can show how many
/// times each agent went back to the model.
/// </summary>
internal sealed class CallCounter(IChatClient inner) : DelegatingChatClient(inner)
{
    public int ModelCalls { get; private set; }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ModelCalls++;
        return base.GetResponseAsync(messages, options, cancellationToken);
    }
}

internal partial class Program
{
    // Appended after the model's top-level statements to form a runnable .NET 10
    // file-based app: local functions first, type declarations last.
    private const string ActionApiSource =
        """
        // ---- action API (appended by the host) ----
        static Order GetOrder(string orderId)
        {
            var n = int.Parse(orderId[2..]);
            return new Order(orderId,
                n % 4 == 1 ? "delayed" : "in transit",
                40 + n * 3.5m,
                n % 2 == 0 ? "PostNord" : "GLS",
                [.. Enumerable.Range(0, 6).Select(i => $"2026-08-{10 + i:00}T0{i}:15:00Z scan at hub-{(n * 7 + i) % 50:00}")],
                "Signature required on delivery. Fragile items in parcel. Contact carrier for rerouting. Customs cleared at DK-CPH.");
        }

        record Order(string Id, string Status, decimal Total, string Carrier, string[] History, string Notes);
        """;
}
