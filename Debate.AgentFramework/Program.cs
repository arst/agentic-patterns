// Debate: two agents argue OPPOSING positions over N rounds — each round a
// debater sees the opponent's last argument and must rebut it — then a judge
// agent rules with a structured verdict (winner + reasoning).
// Contrast with Voting.AgentFramework: voters answer INDEPENDENTLY and never
// see each other; their value is decorrelated ballots. Debate's value is the
// interaction itself — arguments get stress-tested through rebuttal, so weak
// points surface before the judge rules.
// Each debater keeps its own session across rounds (its argument history),
// so rebuttals stay consistent with its earlier claims.

using Microsoft.Agents.AI;
using Shared;

const int Rounds = 3;

const string Question =
    "Should a 5-person startup build its product as a monolith or as microservices?";

ChatClientAgent MakeDebater(string name, string position) => new(
    Settings.ChatClient,
    name: name,
    instructions: $"""
                   You are a debater arguing that {position}.
                   Argue this position and no other — never concede the debate.
                   When shown the opponent's argument, rebut it directly, point by point,
                   then reinforce your own case. Be concrete and concise (max ~120 words).
                   """);

var pro = MakeDebater("MonolithAdvocate", "the startup should build a monolith");
var con = MakeDebater("MicroservicesAdvocate", "the startup should build microservices");

AIAgent judge = new ChatClientAgent(
    Settings.ChatClient,
    name: "Judge",
    instructions: """
                  You are an impartial judge of a structured debate. Read the full
                  transcript, weigh which side's arguments survived rebuttal best,
                  and rule. Judge argument quality, not your own prior opinion.
                  """);

Console.WriteLine("=== Debate ===\n");
Console.WriteLine($"Question: {Question}\n");

// Each debater gets its own session — memory of its own line of argument.
var proSession = await pro.CreateSessionAsync();
var conSession = await con.CreateSessionAsync();

var transcript = new List<string>();

async Task<string> Speak(ChatClientAgent agent, AgentSession session, string prompt)
{
    var text = (await agent.RunAsync(prompt, session)).Text.Trim();
    transcript.Add($"[{agent.Name}] {text}");
    Console.WriteLine($"[{agent.Name}]\n{text}\n");
    return text;
}

string proLast = "", conLast = "";
for (var round = 1; round <= Rounds; round++)
{
    Console.WriteLine($"---- Round {round} ----\n");

    proLast = await Speak(pro, proSession, round == 1
        ? $"Debate question: {Question}\nMake your opening argument."
        : $"Your opponent argued:\n{conLast}\nRebut and continue your case.");

    conLast = await Speak(con, conSession, round == 1
        ? $"Debate question: {Question}\nYour opponent opened with:\n{proLast}\nMake your opening argument and rebut them."
        : $"Your opponent argued:\n{proLast}\nRebut and continue your case.");
}

Console.WriteLine("---- Judgement ----\n");

var verdict = (await judge.RunAsync<Verdict>(
    $"""
     Debate question: {Question}

     Transcript:
     {string.Join("\n\n", transcript)}

     Rule on the debate.
     """)).Result;

Console.WriteLine($"Winner:    {verdict.Winner}");
Console.WriteLine($"Reasoning: {verdict.Reasoning}");
Console.WriteLine($"Strongest point of the debate: {verdict.StrongestPoint}");

internal record Verdict(string Winner, string Reasoning, string StrongestPoint);
