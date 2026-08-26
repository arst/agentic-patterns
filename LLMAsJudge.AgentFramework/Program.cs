using LLMAsJudge.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Shared;

if (args.FirstOrDefault() == "--selfcheck") { SelfCheck(); return; }

var chatClient = Settings.ChatClient;
var chatConfig = new ChatConfiguration(chatClient);

const string policy =
    "TechCorp laptops include a two-year limited warranty. Defective products may be " +
    "returned within 30 days with the order number.";

var agent = new ChatClientAgent(chatClient, name: "SupportAgent",
    instructions: $"You are a TechCorp support agent. Use only this policy: {policy}. Answer concisely.");

string[] questions =
[
    "What warranty do TechCorp laptops come with?",
    "How long do I have to return a defective product?"
];

Console.WriteLine("==== LLM-as-Judge: scoring answers ====\n");
foreach (var q in questions)
{
    var answer = (await agent.RunAsync(q)).Text;
    Console.WriteLine($"Q: {q}\nA: {answer}");

    IList<ChatMessage> conversation = [new(ChatRole.User, q)];
    var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, answer));

    foreach (var (name, eval, ctx) in Evaluators())
    {
        var result = await eval.EvaluateAsync(conversation, response, chatConfig,
            ctx is null ? null : [ctx]);
        var metric = result.Get<NumericMetric>(result.Metrics.Keys.First());
        // A null value means the judge's verdict could not be read — print that plainly rather
        // than a blank column that reads like a zero.
        Console.WriteLine($"   {name,-14}: {metric.Value?.ToString() ?? "INDETERMINATE"}  ({metric.Reason})");
    }
    Console.WriteLine();
}

// ---- Pairwise comparison across randomized position orderings ----
Console.WriteLine("==== Pairwise comparison (position-bias probe) ====\n");
const string pairwiseQuestion = "What warranty do TechCorp laptops come with?";
var good = "TechCorp laptops come with a two-year limited warranty.";
var vague = "TechCorp offers a warranty on its laptops for a period of time.";

const int orderings = 5;
var picks = new List<bool?>();
for (var i = 0; i < orderings; i++)
{
    var goodInPositionA = Random.Shared.Next(2) == 0;
    var candidateA = goodInPositionA ? good : vague;
    var candidateB = goodInPositionA ? vague : good;
    var verdict = await PairwiseWinnerAsync(pairwiseQuestion, candidateA, candidateB);
    picks.Add(JudgeParsing.Resolve(verdict, goodInPositionA));
}

var report = JudgeParsing.Summarize(picks);
Console.WriteLine(
    $"Good wins: {report.ReferenceWins}  Vague wins: {report.OtherWins}  Indeterminate: {report.Indeterminate}");
Console.WriteLine($"Position-bias rate (of determinate verdicts): {report.PositionBiasRate:P0}");
Console.WriteLine(report.PositionBiasRate > 0
    ? "► Position bias DETECTED: the weaker candidate won at least once across randomized orderings."
    : "► Consistent verdict across randomized orderings.");

IEnumerable<(string, IEvaluator, EvaluationContext?)> Evaluators() =>
[
    ("Relevance", new RelevanceEvaluator(), null),
    ("Coherence", new CoherenceEvaluator(), null),
    ("Groundedness", new GroundednessEvaluator(), new GroundednessEvaluatorContext(policy)),
    ("RubricScore", new RubricJudgeEvaluator(), null)
];

async Task<Preference> PairwiseWinnerAsync(string q, string candidateA, string candidateB)
{
    var prompt =
        $$"""
         Question: {{q}}
         Candidate A: {{candidateA}}
         Candidate B: {{candidateB}}
         Which answer is better? Respond JSON: {"winner": "A"} or {"winner": "B"}.
         """;
    var r = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)],
        new ChatOptions { Temperature = 0f, ResponseFormat = ChatResponseFormat.Json });
    return JudgeParsing.Parse(r.Text);
}

static void SelfCheck()
{
    // JudgeParsing.Parse: strict verdicts, Indeterminate on anything unrecognised or unparseable.
    if (JudgeParsing.Parse("{\"winner\":\"A\"}") != Preference.A) throw new Exception("A misparsed");
    if (JudgeParsing.Parse("{\"winner\":\"B\"}") != Preference.B) throw new Exception("B misparsed");
    if (JudgeParsing.Parse("{\"winner\":\"a\"}") != Preference.Indeterminate) throw new Exception("lowercase not indeterminate");
    if (JudgeParsing.Parse("garbage") != Preference.Indeterminate) throw new Exception("garbage not indeterminate");
    if (JudgeParsing.Parse(null) != Preference.Indeterminate) throw new Exception("null not indeterminate");
    if (JudgeParsing.Parse("") != Preference.Indeterminate) throw new Exception("empty not indeterminate");

    // JudgeParsing.Resolve: verdict + slot -> did the reference candidate win?
    if (JudgeParsing.Resolve(Preference.A, referenceInPositionA: true) != true) throw new Exception("resolve A/A wrong");
    if (JudgeParsing.Resolve(Preference.B, referenceInPositionA: true) != false) throw new Exception("resolve B/A wrong");
    if (JudgeParsing.Resolve(Preference.Indeterminate, referenceInPositionA: true) is not null) throw new Exception("resolve indeterminate wrong");

    // JudgeParsing.Summarize: same candidate wins every time -> no bias; a flip -> bias.
    if (JudgeParsing.Summarize([true, true, true, true, true]).PositionBiasRate != 0) throw new Exception("false positive bias");
    if (JudgeParsing.Summarize([true, false, true, true, true]).PositionBiasRate <= 0) throw new Exception("missed bias");
    var allIndeterminate = JudgeParsing.Summarize([null, null, null]);
    if (allIndeterminate.Indeterminate != 3 || allIndeterminate.PositionBiasRate != 0) throw new Exception("indeterminate handling wrong");

    Console.WriteLine("selfcheck ok");
}
