using System.Text.Json;
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
        Console.WriteLine($"   {name,-14}: {metric.Value}  ({metric.Reason})");
    }
    Console.WriteLine();
}

// ---- Pairwise comparison with position swap ----
Console.WriteLine("==== Pairwise comparison (position-bias probe) ====\n");
const string pairwiseQuestion = "What warranty do TechCorp laptops come with?";
var good = "TechCorp laptops come with a two-year limited warranty.";
var vague = "TechCorp offers a warranty on its laptops for a period of time.";

var firstWins = await PairwiseWinnerAsync(pairwiseQuestion, good, vague);   // good in position A
var swappedWins = await PairwiseWinnerAsync(pairwiseQuestion, vague, good); // good in position B
// Winner is reported as "A" or "B"; translate to the candidate identity.
var pick1 = firstWins == "A" ? "good" : "vague";
var pick2 = swappedWins == "A" ? "vague" : "good";
Console.WriteLine($"Original order picked: {pick1}");
Console.WriteLine($"Swapped order picked:  {pick2}");
Console.WriteLine(PositionBiasDetected(pick1, pick2)
    ? "► Position bias DETECTED: verdict flipped when candidates were swapped."
    : "► Consistent verdict across positions.");

IEnumerable<(string, IEvaluator, EvaluationContext?)> Evaluators() =>
[
    ("Relevance", new RelevanceEvaluator(), null),
    ("Coherence", new CoherenceEvaluator(), null),
    ("Groundedness", new GroundednessEvaluator(), new GroundednessEvaluatorContext(policy)),
    ("RubricScore", new RubricJudgeEvaluator(), null)
];

async Task<string> PairwiseWinnerAsync(string q, string candidateA, string candidateB)
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
    var winner = JsonSerializer.Deserialize<Dictionary<string, string>>(r.Text,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))?.GetValueOrDefault("winner");
    return winner == "B" ? "B" : "A";
}

// Bias is present when the same candidate does NOT win regardless of its slot.
static bool PositionBiasDetected(string pickOriginal, string pickSwapped) =>
    pickOriginal != pickSwapped;

static void SelfCheck()
{
    // Same candidate wins in both orders -> no bias. Different -> bias.
    if (PositionBiasDetected("good", "good")) throw new Exception("false positive");
    if (!PositionBiasDetected("good", "vague")) throw new Exception("missed flip");
    Console.WriteLine("selfcheck ok");
}
