using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// ── Two specialised agents — one per phase ───────────────────
//
// In MAF, splitting phases into separate agents is idiomatic.
// Each agent has focused instructions and no knowledge of the
// other's role, which keeps prompts clean and behaviour predictable.

// Agent 1: reads context, produces margin notes only
var noteAgent = new ChatClientAgent(Settings.ChatClient,
    name: "AnswerAgent",
    instructions: """
                  You are a knowledgeable assistant. You will be given an annotated context
                  (original text interleaved with margin notes) and a question.

                  Follow these steps explicitly:
                  1. Write a [Note on Question] that reflects on what the question is really
                     asking and which parts of the annotated context are most relevant.
                  2. Then write [Final Answer] using the annotated context to inform your response.

                  Format:
                  [Note on Question]: <your reflection on the question>

                  [Final Answer]: <your answer, grounded in the annotated context>
                  """);
var answerAgent = new ChatClientAgent(Settings.ChatClient,
    name: "AnswerAgent",
    instructions: """
                  You are a knowledgeable assistant. You will be given an annotated context
                  (original text interleaved with margin notes) and a question.

                  Follow these steps explicitly:
                  1. Write a [Note on Question] that reflects on what the question is really
                     asking and which parts of the annotated context are most relevant.
                  2. Then write [Final Answer] using the annotated context to inform your response.

                  Format:
                  [Note on Question]: <your reflection on the question>

                  [Final Answer]: <your answer, grounded in the annotated context>
                  """);

var context = """
              Section 1:
              The Roman Empire reached its greatest territorial extent under Emperor Trajan in 117 AD,
              spanning from Britain in the northwest to Mesopotamia in the east. The empire's
              administrative system relied heavily on a network of roads and a professional army.

              Section 2:
              Economic pressures in the 3rd century AD led to a period known as the Crisis of the
              Third Empire. Rampant inflation, military coups, and external invasions severely
              weakened central authority. Emperors struggled to maintain control of distant provinces.

              Section 3:
              The empire was formally divided into Western and Eastern halves in 285 AD under
              Diocletian, as a pragmatic response to the difficulty of governing such a vast territory
              from a single capital. The Eastern half would survive as the Byzantine Empire until 1453.
              """;

var question = "What were the key factors that made the Roman Empire difficult to sustain?";
Console.WriteLine($"Question: {question}\n");

var answer = await RunSelfNoteAsync(noteAgent, answerAgent, context, question);

Console.WriteLine("=== Final Answer ===");
Console.WriteLine(answer);


async Task<string> RunSelfNoteAsync(
    AIAgent notingAgent,
    AIAgent answering,
    string ctx,
    string q)
{
    // NoteAgent reads context, produces notes
    // The question is deliberately withheld — the agent annotates
    // based on content alone, not on what it thinks the answer is.

    Console.WriteLine("── Phase 1: NoteAgent generating context notes ──\n");

    // MAF agents are stateless — no thread needed for single-turn calls.
    // Per-run temperature via ChatClientAgentRunOptions.
    var lowTempOptions = new ChatClientAgentRunOptions(
        new ChatOptions { Temperature = 0.3f }
    );

    var noteResponse = await notingAgent.RunAsync(
        $"Please write margin notes on the following context:\n\n{ctx}",
        options: lowTempOptions
    );
    var notes = noteResponse.Text ?? "";

    Console.WriteLine("Generated Notes:");
    Console.WriteLine(notes);
    Console.WriteLine();

    // Interleave notes with original context
    // Deterministic step — no LLM call needed here.
    // We parse the notes and weave them into the context text.

    Console.WriteLine("Interleaving notes with context\n");

    var interleavedContext = InterleaveNotesWithContext(ctx, notes);

    Console.WriteLine("Interleaved Context:");
    Console.WriteLine(interleavedContext);
    Console.WriteLine();

    // ── PHASE 3: AnswerAgent sees annotated context + question ─
    // The agent writes a note on the question first (grounding),
    // then produces the final answer.

    Console.WriteLine("AnswerAgent generating self-noted answer\n");

    var answerResponse = await answering.RunAsync([
            new ChatMessage(ChatRole.System,
                "You are a knowledgeable assistant. Use the provided annotated context to answer the question."),
            new ChatMessage(ChatRole.User,
                $"""
                 Annotated context:
                 {interleavedContext}

                 Question: {q}
                 """)
        ],
        options: lowTempOptions
    );

    return answerResponse.Text ?? "";
}

// Same logic as the SK version — parses model-generated notes
// and inserts them after their matching section in the context.
// This is pure string manipulation — no LLM call needed.

string InterleaveNotesWithContext(string ctx, string notes)
{
    var result = new StringBuilder();

    // Parse "[Note on Section N]: <text>" into a dictionary
    var noteMap = new Dictionary<int, string>();
    foreach (var line in notes.Split('\n'))
    {
        var match = Regex.Match(
            line, @"\[Note on Section (\d+)\]:\s*(.+)",
            RegexOptions.IgnoreCase);

        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var sectionNum))
            noteMap[sectionNum] = match.Groups[2].Value.Trim();
    }

    var currentSection = 0;
    foreach (var line in ctx.Split('\n'))
    {
        result.AppendLine(line);

        // Detect "Section N:" headers
        var sectionMatch = Regex.Match(
            line, @"Section (\d+):",
            RegexOptions.IgnoreCase);

        if (sectionMatch.Success &&
            int.TryParse(sectionMatch.Groups[1].Value, out var num))
            currentSection = num;

        // Inject note after the blank line that follows each section
        if (string.IsNullOrWhiteSpace(line) &&
            currentSection > 0 &&
            noteMap.TryGetValue(currentSection, out var note))
        {
            result.AppendLine($"    [Margin Note]: {note}");
            noteMap.Remove(currentSection);
        }
    }

    // Append any unmatched notes at the end
    foreach (var (_, note) in noteMap)
        result.AppendLine($"[Additional Note]: {note}");

    return result.ToString();
}