// Reflexion: verbal reinforcement across EPISODES. Attempt the task, evaluate
// the attempt with a programmatic verifier, and on failure ask the agent to
// write a self-reflection ("what went wrong, what to try differently"). The
// reflection goes into episodic memory, and the NEXT trial re-runs the task
// from scratch with all accumulated reflections injected into the prompt —
// the agent learns from failure without any weight updates.
// Contrast with siblings: SelfCorrectionLoop revises a single answer within
// one pass (critique -> revise the same draft); ExpeL distills general,
// reusable rules from MANY past episodes for future tasks; Reflexion is the
// trial-level retry loop on ONE task, with per-trial verbal feedback.
// The verifier here is C# code, not an LLM — failure is objective, so the
// trial log genuinely shows reflections steering later attempts.

using Microsoft.Agents.AI;
using Shared;

const int MaxTrials = 5;

const string Task =
    "Write one English sentence that is EXACTLY 6 words long, where EVERY word " +
    "starts with the letter 's', no word is used twice, and each word is STRICTLY " +
    "longer (in letters) than the word before it.";

AIAgent solver = new ChatClientAgent(
    Settings.ChatClient,
    name: "Solver",
    instructions: "Solve the task. Output ONLY the sentence — no quotes, no commentary.");

AIAgent reflector = new ChatClientAgent(
    Settings.ChatClient,
    name: "Reflector",
    instructions: """
                  You write self-reflections for a failed task attempt.
                  Given the task, the failed attempt and the verifier's error report,
                  explain in 1-2 sentences what went wrong and what concrete strategy
                  to use on the next attempt. Output only the reflection.
                  """);

// Programmatic verifier — the ground truth the episodes learn against.
static List<string> Verify(string sentence)
{
    var errors = new List<string>();
    var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.Trim('.', ',', '!', '?', ';', ':', '"', '\''))
        .Where(w => w.Length > 0)
        .ToList();

    if (words.Count != 6)
        errors.Add($"sentence has {words.Count} words, required exactly 6");
    foreach (var w in words.Where(w => !w.StartsWith('s') && !w.StartsWith('S')))
        errors.Add($"word '{w}' does not start with 's'");
    errors.AddRange(words.GroupBy(w => w.ToLowerInvariant())
        .Where(g => g.Count() > 1)
        .Select(g => $"word '{g.Key}' is used {g.Count()} times"));
    var lengths = words.Select(w => w.Length).ToList();
    if (lengths.Zip(lengths.Skip(1)).Any(p => p.Second <= p.First))
        errors.Add($"word lengths must strictly increase, got: {string.Join(",", lengths)}");
    return errors;
}

Console.WriteLine("=== Reflexion: episodic retry with self-reflection ===\n");
Console.WriteLine($"Task: {Task}\n");

var reflections = new List<string>(); // episodic memory

for (var trial = 1; trial <= MaxTrials; trial++)
{
    Console.WriteLine($"---- Trial {trial} ----");

    // Fresh attempt each episode; only the reflections carry over.
    var memoryBlock = reflections.Count == 0
        ? ""
        : "\n\nReflections from your previous failed attempts — apply them:\n" +
          string.Join("\n", reflections.Select((r, i) => $"{i + 1}. {r}"));

    var attempt = (await solver.RunAsync(Task + memoryBlock)).Text.Trim();
    Console.WriteLine($"Attempt:    {attempt}");

    var errors = Verify(attempt);
    if (errors.Count == 0)
    {
        Console.WriteLine("Verdict:    PASS");
        Console.WriteLine($"\nSolved in {trial} trial(s) with {reflections.Count} reflection(s) in memory.");
        return;
    }

    Console.WriteLine($"Verdict:    FAIL — {string.Join("; ", errors)}");

    var reflection = (await reflector.RunAsync(
        $"""
         Task: {Task}
         Failed attempt: {attempt}
         Verifier errors: {string.Join("; ", errors)}
         """)).Text.Trim();
    reflections.Add(reflection);
    Console.WriteLine($"Reflection: {reflection}\n");
}

Console.WriteLine($"Gave up after {MaxTrials} trials. Episodic memory held {reflections.Count} reflections.");
