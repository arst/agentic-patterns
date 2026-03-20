using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shared;

var kernel = Settings.Kernel;
var chat = kernel.GetRequiredService<IChatCompletionService>();

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

var answer = await RunSelfNoteAsync(chat, context, question);

Console.WriteLine("=== Final Answer ===");
Console.WriteLine(answer);

async Task<string> RunSelfNoteAsync(
    IChatCompletionService svc,
    string ctx,
    string q)
{
    // Generate notes on the context 
    // The model reads each section and writes margin notes.
    // Crucially, the question is NOT shown yet — this forces
    // the model to annotate based purely on the content,
    // not to reverse-engineer notes that fit the answer.

    Console.WriteLine("Phase 1: Generating context notes\n");

    var noteHistory = new ChatHistory();
    noteHistory.AddSystemMessage("""
                                 You are a careful academic reader. Your job is to read a provided context
                                 and write concise margin notes for each section.

                                 Rules:
                                 - Write notes ONLY on the context provided. Do NOT try to answer any question.
                                 - For each section, identify: key facts, implications, and potential connections.
                                 - Format your notes as:
                                   [Note on Section 1]: <your note>
                                   [Note on Section 2]: <your note>
                                   ... and so on for each section present.
                                 - Be concise but substantive. Each note should be 1-3 sentences.
                                 """);

    noteHistory.AddUserMessage($"Please write margin notes on the following context:\n\n{ctx}");

    var noteSettings = new OpenAIPromptExecutionSettings { Temperature = 0.3 };
    var noteResponse = await svc.GetChatMessageContentAsync(noteHistory, noteSettings);
    var notes = noteResponse.Content ?? "";

    Console.WriteLine("Generated Notes:");
    Console.WriteLine(notes);
    Console.WriteLine();

    // Interleave notes with original context
    // We weave the model's notes back into the context so that
    // when the model reads it again in phase 3, the annotations
    // sit alongside the relevant text — just like margin notes.

    Console.WriteLine("Interleaving notes with context\n");

    var interleavedContext = InterleaveNotesWithContext(ctx, notes);

    Console.WriteLine("Interleaved Context:");
    Console.WriteLine(interleavedContext);
    Console.WriteLine();

    // Generate note on the question, then answer
    // Now the model sees the annotated context + the question.
    // It first writes a note reflecting on what the question
    // is really asking, then produces the final answer.
    // This extra step grounds the answer in the prior annotations.

    Console.WriteLine("Answering with self-notes\n");

    var answerHistory = new ChatHistory();
    answerHistory.AddSystemMessage("""
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

    answerHistory.AddUserMessage(
        $"""
         Annotated context:
         {interleavedContext}

         Question: {q}
         """);

    var answerSettings = new OpenAIPromptExecutionSettings { Temperature = 0.3 };
    var answerResponse = await svc.GetChatMessageContentAsync(answerHistory, answerSettings);

    return answerResponse.Content ?? "";
}


// Parse the model's notes and inserts each one after the
// matching section in the original context.
// Fall back to appending all notes at the end if parsing fails.

string InterleaveNotesWithContext(string ctx, string notes)
{
    var lines = ctx.Split('\n');
    var result = new StringBuilder();

    // Parse notes into a dictionary: section number → note text
    // Expected format: "[Note on Section N]: <text>"
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
    foreach (var line in lines)
    {
        result.AppendLine(line);

        // Detect section headers (e.g. "Section 1:", "Section 2:")
        var sectionMatch = Regex.Match(
            line, @"Section (\d+):",
            RegexOptions.IgnoreCase);

        if (sectionMatch.Success &&
            int.TryParse(sectionMatch.Groups[1].Value, out var num))
            currentSection = num;

        // After each blank line following a section, inject the note
        if (string.IsNullOrWhiteSpace(line) &&
            currentSection > 0 &&
            noteMap.TryGetValue(currentSection, out var note))
        {
            result.AppendLine($"    [Margin Note]: {note}");
            noteMap.Remove(currentSection); // inject once per section
        }
    }

    // Append any remaining notes that didn't match a section
    foreach (var (_, note) in noteMap)
        result.AppendLine($"[Additional Note]: {note}");

    return result.ToString();
}