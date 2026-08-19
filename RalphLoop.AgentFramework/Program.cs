using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Ralph loop: run an agent in a loop with a FRESH context every iteration until a plan
// is satisfied. No conversation carries over — all state lives in files: PLAN.md is the
// task list, PROGRESS.md is the append-only log each iteration leaves for the next one,
// and the produced files are the actual work. (The original Ralph pattern communicates
// progress via git history; this demo uses PROGRESS.md for the same role.)
// Contrast Reflexion, which retries WITHIN one growing context, and DurableExecution,
// which checkpoints a workflow — here the context is deliberately thrown away each turn.

var workDir = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "ralph-loop", Guid.NewGuid().ToString("N"))).FullName;
Console.WriteLine($"Workdir: {workDir}\n");

File.WriteAllText(Path.Combine(workDir, "PLAN.md"),
    """
    # Plan: two-day Copenhagen travel guide

    - [ ] research.md — list 6 sights with a one-line note each
    - [ ] itinerary.md — a 2-day itinerary that uses the sights from research.md
    - [ ] summary.md — a 5-sentence pitch for the trip, consistent with both files
    """);
File.WriteAllText(Path.Combine(workDir, "PROGRESS.md"), "# Progress log\n");

AITool[] fileTools =
[
    AIFunctionFactory.Create(() => string.Join("\n", Directory.GetFiles(workDir).Select(Path.GetFileName)),
        "list_files", "List the files in the working directory."),
    AIFunctionFactory.Create((string name) => File.ReadAllText(Path.Combine(workDir, Path.GetFileName(name))),
        "read_file", "Read a file from the working directory."),
    AIFunctionFactory.Create((string name, string content) =>
    {
        File.WriteAllText(Path.Combine(workDir, Path.GetFileName(name)), content);
        return $"Wrote {name} ({content.Length} chars).";
    }, "write_file", "Write (or overwrite) a file in the working directory.")
];

var agent = new ChatClientAgent(Settings.ChatClient,
    """
    You are ONE iteration of a loop; you have no memory of previous iterations.
    First read PLAN.md and PROGRESS.md. Then complete exactly ONE unchecked task
    ("- [ ]") from the plan: create its file, mark the task done ("- [x]") by rewriting
    PLAN.md, and append one line describing what you did to PROGRESS.md.
    If every task is already checked, change nothing. Finally reply with a single line:
    "DONE: <task>" or "ALL-DONE".
    """,
    tools: fileTools);

for (var iteration = 1; iteration <= 8; iteration++)
{
    // A brand-new session per iteration — the fresh context IS the pattern.
    var session = await agent.CreateSessionAsync();
    var response = await agent.RunAsync("Run one iteration of the loop.", session);

    var toolCalls = response.Messages
        .SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count();
    Console.WriteLine($"Iteration {iteration}: {response.Text.ReplaceLineEndings(" ").Trim()}  [{toolCalls} tool calls, context discarded]");

    if (!File.ReadAllText(Path.Combine(workDir, "PLAN.md")).Contains("- [ ]"))
        break;
}

Console.WriteLine($"\n---- PLAN.md ----\n{File.ReadAllText(Path.Combine(workDir, "PLAN.md"))}");
Console.WriteLine($"\n---- PROGRESS.md (how iterations talked to each other) ----\n{File.ReadAllText(Path.Combine(workDir, "PROGRESS.md"))}");
Console.WriteLine($"\n---- Files produced ----\n{string.Join("\n", Directory.GetFiles(workDir).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)"))}");
