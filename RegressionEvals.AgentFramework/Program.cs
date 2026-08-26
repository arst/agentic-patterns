using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.NLP;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using RegressionEvals.AgentFramework;
using Shared;

if (args.FirstOrDefault() == "--selfcheck") { SelfCheck(); return; }

var chatClient = Settings.ChatClient;
var baseDir = AppContext.BaseDirectory;
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var allCases = JsonSerializer.Deserialize<List<GoldenCase>>(
    File.ReadAllText(Path.Combine(baseDir, "golden-cases.json")), web)!;
var (cases, awaitingReview) = CasePartition.Partition(allCases);

// The canonical "production trace -> candidate case" pipeline: extraction pulls the question and
// the OBSERVED answer straight from a recorded EvaluationAndMonitoring trajectory. A trace is
// ground truth about what HAPPENED, never about what SHOULD have happened, so it lands in
// candidates/ for a reviewer to supply/confirm the expected answer - it never joins `cases` above.
var candidate = ExtractTraceCase(Path.Combine(baseDir, "sample-run-trace.json"));
var candidatesDir = Path.Combine(baseDir, "candidates");
Directory.CreateDirectory(candidatesDir);
// ponytail: candidates/ lives under bin/, build output wiped by `dotnet clean`, so a candidate
// does not survive here to actually be reviewed. A real repo commits candidates beside
// golden-cases.json and reviews the promotion (filling in expectedAnswer/tier/reviewedBy) in a PR.
File.WriteAllText(Path.Combine(candidatesDir, $"{candidate.Id}.json"), JsonSerializer.Serialize(new
{
    candidate.Id, candidate.Question, candidate.ObservedAnswer, candidate.SourceTrace,
    reviewedBy = (string?)null
}, web));

// Two different states, two different counts: an awaiting-review GoldenCase already has an
// ExpectedAnswer and Tier and just needs sign-off, while a CandidateCase has neither and needs a
// reviewer to write the expected answer from scratch. Folding them into one number would call a
// fully-specified unsigned golden case a "candidate", contradicting the distinction this task exists
// to enforce.
Console.WriteLine($"{awaitingReview.Count} golden case(s) awaiting sign-off - not evaluated.");
Console.WriteLine("1 candidate case(s) awaiting review - not evaluated.\n");

const string policy =
    "TechCorp laptops include a two-year limited warranty. Defective products may be " +
    "returned within 30 days with the order number.";
var agent = new ChatClientAgent(chatClient, name: "SupportAgent",
    instructions: $"You are a TechCorp support agent. Use only this policy: {policy}. Answer concisely.");

var reporting = DiskBasedReportingConfiguration.Create(
    storageRootPath: Path.Combine(baseDir, "eval-results"),
    evaluators: [new EquivalenceEvaluator(), new F1Evaluator()],
    chatConfiguration: new ChatConfiguration(chatClient),
    enableResponseCaching: true,
    executionName: "regression");

Console.WriteLine("==== Regression eval suite ====\n");
var failures = 0;
foreach (var c in cases)
{
    await using var run = await reporting.CreateScenarioRunAsync($"Regression.{c.Id}");
    var answer = (await agent.RunAsync(c.Question)).Text;

    var (passed, detail) = c.Tier switch
    {
        "contains" => (answer.Contains(c.ExpectedAnswer, StringComparison.OrdinalIgnoreCase),
                    $"contains \"{c.ExpectedAnswer}\""),
        "nlp" => await NlpTierAsync(run, c, answer),
        "judge" => await JudgeTierAsync(run, c, answer),
        _ => (false, $"unknown tier {c.Tier}")
    };

    if (!passed) failures++;
    Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {c.Id} ({c.Tier}) — {detail}");
    Console.WriteLine($"        answer: {answer}");
}

Console.WriteLine($"\n{cases.Count - failures}/{cases.Count} passed. " +
                  "Re-run to see cached (zero-call) evaluation; generate an HTML report with:");
Console.WriteLine($"  dotnet tool run aieval report --path {Path.Combine(baseDir, "eval-results")} --output report.html");
Environment.Exit(CasePartition.GateExitCode(cases.Count, failures));

async Task<(bool, string)> NlpTierAsync(ScenarioRun run, GoldenCase c, string answer)
{
    var result = await run.EvaluateAsync(
        [new ChatMessage(ChatRole.User, c.Question)],
        new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
        [new F1EvaluatorContext(c.ExpectedAnswer)]);
    var f1 = result.Get<NumericMetric>(F1Evaluator.F1MetricName);
    var pass = (f1.Value ?? 0) >= 0.3;   // token-overlap floor; NLP metric, no model call
    return (pass, $"F1={f1.Value:F2} (>=0.30)");
}

async Task<(bool, string)> JudgeTierAsync(ScenarioRun run, GoldenCase c, string answer)
{
    var result = await run.EvaluateAsync(
        [new ChatMessage(ChatRole.User, c.Question)],
        new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
        [new EquivalenceEvaluatorContext(c.ExpectedAnswer)]);
    var eq = result.Get<NumericMetric>(EquivalenceEvaluator.EquivalenceMetricName);
    var pass = eq.Interpretation is { Failed: false };
    return (pass, $"equivalence={eq.Value} ({eq.Interpretation?.Rating})");
}

CandidateCase ExtractTraceCase(string tracePath)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(tracePath));
    var firstCall = doc.RootElement.GetProperty("modelCalls")[0];
    string TextOf(string arrayName) => firstCall.GetProperty(arrayName)[0]
        .GetProperty("contents")[0].GetProperty("payload").GetProperty("value").GetString()!;
    return new CandidateCase(
        "from-trace", TextOf("messages"), TextOf("responseMessages"), Path.GetFileName(tracePath));
}

static void SelfCheck()
{
    // Extraction pulls the question and answer out of the trace fixture shape.
    var json = """
      {"modelCalls":[{"messages":[{"contents":[{"payload":{"value":"Q?"}}]}],
      "responseMessages":[{"contents":[{"payload":{"value":"A."}}]}]}]}
      """;
    using var doc = JsonDocument.Parse(json);
    var call = doc.RootElement.GetProperty("modelCalls")[0];
    var q = call.GetProperty("messages")[0].GetProperty("contents")[0].GetProperty("payload").GetProperty("value").GetString();
    if (q != "Q?") throw new Exception("extraction broken");
    Console.WriteLine("selfcheck ok");
}
