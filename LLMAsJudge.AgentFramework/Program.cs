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

// A balanced 3/2 split, shuffled: both slots are always sampled, so the position statistic is
// always defined. Five independent coin flips land every trial in one slot 6.25% of the time,
// and a statistic conditioned on position cannot be computed from a single slot.
var slots = new[] { true, true, true, false, false };
Random.Shared.Shuffle(slots);

var trials = new List<Trial>();
foreach (var goodInPositionA in slots)
{
    var candidateA = goodInPositionA ? good : vague;
    var candidateB = goodInPositionA ? vague : good;
    var verdict = await PairwiseWinnerAsync(pairwiseQuestion, candidateA, candidateB);
    trials.Add(new Trial(goodInPositionA, verdict));
}

var report = JudgeParsing.Summarize(trials);
Console.WriteLine(
    $"Good wins: {report.ReferenceWins}  Vague wins: {report.OtherWins}  Indeterminate: {report.Indeterminate}");
Console.WriteLine(report.PositionSwing is { } swing
    ? $"Position swing (good answer's win rate in slot A vs slot B): {swing:P0}"
    : "Position swing: not measurable — one slot produced no determinate verdict.");
// Five trials measure nothing at a useful confidence: one differing verdict is as likely to be
// ordinary sampling noise as real positional preference. This probe shows HOW to measure position
// dependence, so it reports what it observed and stops short of claiming an effect.
Console.WriteLine(report.PositionSwing switch
{
    > 0 => "► Position-dependent verdicts OBSERVED in this probe: the same pair got a different "
           + "verdict depending on which slot the better answer sat in. Five trials cannot separate "
           + "that from sampling noise — rerun at a real trial count before calling it bias.",
    0 => "► No position dependence observed in this small probe: the verdict did not change when "
         + "the candidates swapped slots. Absence at n=5 is not evidence of absence.",
    _ => "► Position dependence not measurable this run."
});

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

    // JudgeParsing.Summarize: the swing is position-conditioned, so it must separate "the judge
    // depends on the slot" from "the judge is simply wrong". Fixed inputs, no randomness.
    Trial InA(Preference v) => new(ReferenceInPositionA: true, v);
    Trial InB(Preference v) => new(ReferenceInPositionA: false, v);

    // Judge always picks the reference candidate, whichever slot it is in: no position dependence.
    if (JudgeParsing.Summarize([InA(Preference.A), InA(Preference.A), InB(Preference.B), InB(Preference.B)])
        .PositionSwing != 0) throw new Exception("false positive bias");

    // Judge is simply wrong — always picks the other candidate, in both slots. Still no position
    // dependence: the old rate reported 100% bias here, which is the defect this replaces.
    var alwaysWrong = JudgeParsing.Summarize(
        [InA(Preference.B), InA(Preference.B), InB(Preference.A), InB(Preference.A)]);
    if (alwaysWrong.PositionSwing != 0) throw new Exception("wrongness misreported as position bias");
    if (alwaysWrong.OtherWins != 4) throw new Exception("win counting wrong");

    // Judge always picks whatever sits in slot A: total position dependence.
    if (JudgeParsing.Summarize([InA(Preference.A), InA(Preference.A), InB(Preference.A), InB(Preference.A)])
        .PositionSwing != 1) throw new Exception("missed bias");

    // Five verdicts drawn in the same slot say nothing about position, however lopsided.
    if (JudgeParsing.Summarize([InA(Preference.A), InA(Preference.B), InA(Preference.A)])
        .PositionSwing is not null) throw new Exception("one-slot swing should be unmeasurable");

    var allIndeterminate = JudgeParsing.Summarize(
        [InA(Preference.Indeterminate), InB(Preference.Indeterminate), InA(Preference.Indeterminate)]);
    if (allIndeterminate.Indeterminate != 3 || allIndeterminate.PositionSwing is not null) throw new Exception("indeterminate handling wrong");

    Console.WriteLine("selfcheck ok");
}
